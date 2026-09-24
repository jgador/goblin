// Generate selected Kubernetes and Agent Sandbox JSON models from pinned schemas.
// Run from the repository root: dotnet run --file backend/scripts/GenerateKubernetes.cs
#:property Nullable=enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

return KubernetesGeneratorApp.Run(args);

internal static class KubernetesGeneratorApp
{
    internal static int Run(string[] args)
    {
        if (args.Any(arg => arg is not ("--check" or "--self-test")))
        {
            Console.Error.WriteLine("Usage: dotnet run --file backend/scripts/GenerateKubernetes.cs -- [--check] [--self-test]");
            return 2;
        }

        try
        {
            if (args.Contains("--self-test"))
            {
                KubernetesGeneratorTests.Run();
                Console.WriteLine("Kubernetes generator tests passed.");
            }

            if (args.Length == 1 && args[0] == "--self-test")
                return 0;

            var scriptDirectory = Path.GetDirectoryName(ScriptPath())!;
            var backendDirectory = Path.GetFullPath(Path.Combine(scriptDirectory, ".."));
            var schemaDirectory = Path.Combine(backendDirectory, "schemas", "kubernetes");
            var outputDirectory = Path.Combine(backendDirectory, "src", "Goblin.Execution", "Kubernetes", "Generated");
            var selectionPath = Path.Combine(schemaDirectory, "selection.json");

            using var selectionDocument = JsonDocument.Parse(File.ReadAllBytes(selectionPath));
            var selection = selectionDocument.RootElement;
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            using var sandboxDocument = ReadSource(selection, "agentSandbox", schemaDirectory, hashes);
            using var kubernetesDocument = ReadSource(selection, "kubernetes", schemaDirectory, hashes);
            var definitions = kubernetesDocument.RootElement.GetProperty("definitions")
                .EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);

            hashes.Add("selection.json", Sha256(File.ReadAllBytes(selectionPath)));
            var files = new KubernetesGenerator(selection, sandboxDocument.RootElement, definitions, hashes).Generate();
            return WriteOrCheck(files, outputDirectory, args.Contains("--check"));
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static string ScriptPath([CallerFilePath] string path = "") => path;

    private static JsonDocument ReadSource(
        JsonElement selection, string key, string schemaDirectory, Dictionary<string, string> hashes)
    {
        var entry = selection.GetProperty("sources").GetProperty(key);
        var filename = entry.GetProperty("file").GetString()!;
        if (Path.GetFileName(filename) != filename)
            throw new InvalidOperationException($"Invalid schema filename: {filename}");

        var data = File.ReadAllBytes(Path.Combine(schemaDirectory, filename));
        var actual = Sha256(data);
        if (actual != entry.GetProperty("sha256").GetString())
            throw new InvalidOperationException($"Schema checksum mismatch: {filename}");
        hashes.Add(filename, actual);

        if (filename.EndsWith(".gz", StringComparison.Ordinal))
        {
            using var compressed = new MemoryStream(data);
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var uncompressed = new MemoryStream();
            gzip.CopyTo(uncompressed);
            data = uncompressed.ToArray();
        }

        if (entry.TryGetProperty("rawSha256", out var rawHash) && Sha256(data) != rawHash.GetString())
            throw new InvalidOperationException($"Raw schema checksum mismatch: {filename}");
        return JsonDocument.Parse(data);
    }

    private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static int WriteOrCheck(Dictionary<string, string> files, string directory, bool check)
    {
        var current = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .ToDictionary(path => Path.GetFileName(path)!, path => path, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var changed = files.Where(file => !current.TryGetValue(file.Key, out var path)
                || File.ReadAllText(path) != file.Value)
            .Select(file => file.Key).Order(StringComparer.Ordinal).ToList();
        var stale = current.Keys.Except(files.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        if (check)
        {
            if (changed.Count > 0 || stale.Count > 0)
            {
                Console.Error.WriteLine("Kubernetes generation is out of date: " + string.Join(", ", changed.Concat(stale)));
                return 1;
            }

            Console.WriteLine($"Kubernetes generation is current ({files.Count - 1} model files).");
            return 0;
        }

        Directory.CreateDirectory(directory);
        foreach (var name in stale)
            File.Delete(current[name]);
        foreach (var (name, content) in files)
            File.WriteAllText(Path.Combine(directory, name), content);
        Console.WriteLine($"Generated {files.Count - 1} selected Kubernetes model files.");
        return 0;
    }
}

internal sealed class KubernetesGenerator
{
    private const string Namespace = "Goblin.Execution.Kubernetes";
    private const string Header = "// <auto-generated />\n"
        + "// Source: backend/schemas/kubernetes/selection.json; regenerate with dotnet run --file backend/scripts/GenerateKubernetes.cs\n"
        + "#nullable enable\n"
        + "using System;\nusing System.Collections.Generic;\nusing System.Text.Json.Serialization;\n\n"
        + $"namespace {Namespace};\n\n";
    private readonly JsonElement _selection;
    private readonly JsonElement _sandbox;
    private readonly Dictionary<string, JsonElement> _definitions;
    private readonly Dictionary<string, string> _sourceHashes;
    private readonly Dictionary<string, SelectedModel> _models = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _enums = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _referenceNames = new(StringComparer.Ordinal);
    private bool _usesIntOrString;

    internal IEnumerable<string> ModelNames => _models.Keys;

    internal KubernetesGenerator(
        JsonElement selection,
        JsonElement sandbox,
        Dictionary<string, JsonElement> definitions,
        Dictionary<string, string>? sourceHashes = null)
    {
        _selection = selection;
        _sandbox = sandbox;
        _definitions = definitions;
        _sourceHashes = sourceHashes ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    internal static string Pascal(string value) => string.Concat(
        Regex.Matches(value, "[A-Za-z0-9]+")
            .Select(match => char.ToUpperInvariant(match.Value[0]) + match.Value[1..]));

    internal static Dictionary<string, SelectionNode> Tree(IEnumerable<string> paths)
    {
        var result = new Dictionary<string, SelectionNode>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var parts = path.Split('.');
            if (parts.Any(part => part.Length == 0))
                throw new InvalidOperationException($"Invalid selected path: {path}");
            var target = result;
            foreach (var part in parts)
            {
                if (!target.TryGetValue(part, out var node))
                    target.Add(part, node = new SelectionNode());
                target = node.Children;
            }
        }
        return result;
    }

    private static void Merge(Dictionary<string, SelectionNode> target, Dictionary<string, SelectionNode> source)
    {
        foreach (var (key, child) in source)
        {
            if (!target.TryGetValue(key, out var node))
                target.Add(key, node = new SelectionNode());
            Merge(node.Children, child.Children);
        }
    }

    private (JsonElement Schema, string Name) ResolveWithOverride(JsonElement schema, string name, string path)
    {
        if (!_selection.TryGetProperty("overrides", out var overrides)
            || !overrides.TryGetProperty(path, out var definitionName))
            return Resolve(schema, name);
        var reference = definitionName.GetString()!;
        if (!_definitions.TryGetValue(reference, out schema))
            throw new InvalidOperationException($"Unknown override definition at {path}: {reference}");
        name = reference.Split('.')[^1];
        RememberReference(name, reference);
        return Resolve(schema, name);
    }

    private (JsonElement Schema, string Name) Resolve(JsonElement schema, string name)
    {
        while (schema.TryGetProperty("$ref", out var reference))
        {
            var refName = reference.GetString()!.Split('/')[^1];
            if (!_definitions.TryGetValue(refName, out schema))
                throw new InvalidOperationException($"Unresolved Kubernetes reference: {refName}");
            name = refName.Split('.')[^1];
            RememberReference(name, refName);
        }
        return (schema, name);
    }

    private void RememberReference(string name, string reference)
    {
        if (_referenceNames.TryGetValue(name, out var previous) && previous != reference)
            throw new InvalidOperationException($"Colliding Kubernetes type name: {name}");
        _referenceNames[name] = reference;
    }

    internal void Select(
        JsonElement schema, Dictionary<string, SelectionNode> fields,
        string name, string path, bool partial)
    {
        (schema, name) = ResolveWithOverride(schema, name, path);
        if (schema.TryGetProperty("x-kubernetes-int-or-string", out var intOrString)
            && intOrString.ValueKind == JsonValueKind.True)
        {
            RequireNoChildren(fields, path);
            _usesIntOrString = true;
            return;
        }
        if (schema.TryGetProperty("enum", out var enumValues))
        {
            RequireNoChildren(fields, path, "Enum");
            if (Kind(schema) != "string")
                throw new InvalidOperationException($"Unsupported enum: {path}");
            _enums.TryAdd(name, enumValues.EnumerateArray().Select(value => value.GetString()!).ToList());
            return;
        }
        if (Kind(schema) == "array")
        {
            Select(schema.GetProperty("items"), fields, name + "Item", path + "[]", partial);
            return;
        }
        var hasProperties = schema.TryGetProperty("properties", out var properties);
        if (Kind(schema) == "object" && !hasProperties)
        {
            if (!schema.TryGetProperty("additionalProperties", out var values)
                || values.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"Untyped selected object: {path}");
            Select(values, fields, name + "Value", path + "{}", partial);
            return;
        }
        if (Kind(schema) == "object")
        {
            if (!_models.TryGetValue(name, out var model))
                _models.Add(name, model = new SelectedModel(schema, partial));
            if (!JsonElement.DeepEquals(model.Schema, schema) || model.Partial != partial)
                throw new InvalidOperationException($"Conflicting model schema: {name}");
            Merge(model.Fields, fields);
            foreach (var (key, children) in fields)
            {
                if (!properties.TryGetProperty(key, out var child))
                    throw new InvalidOperationException($"Unknown selected field: {path}.{key}");
                Select(child, children.Children, name + Pascal(key), path + "." + key, partial);
            }
            return;
        }

        RequireNoChildren(fields, path);
        if (Kind(schema) is not ("string" or "integer" or "number" or "boolean"))
            throw new InvalidOperationException($"Unsupported selected schema at {path}: {schema.GetRawText()}");
    }

    private static void RequireNoChildren(Dictionary<string, SelectionNode> fields, string path, string kind = "Scalar")
    {
        if (fields.Count > 0)
            throw new InvalidOperationException($"{kind} has descendants: {path}");
    }

    private static string? Kind(JsonElement schema) => schema.TryGetProperty("type", out var type)
        ? type.GetString() : null;

    private string TypeName(JsonElement schema, string name, string path)
    {
        (schema, name) = ResolveWithOverride(schema, name, path);
        if (schema.TryGetProperty("x-kubernetes-int-or-string", out var intOrString)
            && intOrString.ValueKind == JsonValueKind.True)
            return "KubernetesIntOrString";
        if (schema.TryGetProperty("enum", out _))
            return name;
        switch (Kind(schema))
        {
            case "string":
                return schema.TryGetProperty("format", out var format) && format.GetString() == "date-time"
                    ? "DateTimeOffset" : "string";
            case "integer":
                return schema.TryGetProperty("format", out var integerFormat) && integerFormat.GetString() == "int32"
                    ? "int" : "long";
            case "number": return "double";
            case "boolean": return "bool";
            case "array":
                return "List<" + TypeName(schema.GetProperty("items"), name + "Item", path + "[]") + ">";
            case "object" when !schema.TryGetProperty("properties", out _):
                if (!schema.TryGetProperty("additionalProperties", out var values)
                    || values.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException($"Untyped selected object: {path}");
                return "Dictionary<string, " + TypeName(values, name + "Value", path + "{}") + ">";
            case "object": return name;
            default:
                throw new InvalidOperationException($"Unsupported selected schema at {path}: {schema.GetRawText()}");
        }
    }

    private static string Literal(string value) => "\""
        + JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping) + "\"";

    private static string EnumBody(string name, List<string> values)
    {
        var members = values.Select(Pascal).ToList();
        if (members.Distinct(StringComparer.Ordinal).Count() != members.Count)
            throw new InvalidOperationException($"Enum member collision: {name}");
        var lines = new List<string>
        {
            $"[JsonConverter(typeof(KubernetesStringEnumConverter<{name}>))]",
            $"public enum {name}", "{",
        };
        foreach (var (member, value) in members.Zip(values))
        {
            lines.Add($"    [JsonStringEnumMemberName({Literal(value)})]");
            lines.Add($"    {member},");
        }
        lines.Add("}");
        return string.Join("\n", lines);
    }

    internal string ModelBody(string name)
    {
        var model = _models[name];
        if (model.Fields.Count == 0)
            throw new InvalidOperationException($"Selected object has no fields: {name}");
        var lines = new List<string> { $"public sealed class {name}", "{" };
        var properties = model.Schema.GetProperty("properties");
        foreach (var key in model.Fields.Keys.Order(StringComparer.Ordinal))
        {
            var property = Pascal(key);
            var child = properties.GetProperty(key);
            var path = name + "." + key;
            var required = !model.Partial && model.Schema.TryGetProperty("required", out var requiredFields)
                && requiredFields.EnumerateArray().Any(value => value.GetString() == key);
            var type = TypeName(child, name + property, path);
            lines.Add($"    [JsonPropertyName({Literal(key)})]");
            if (required)
                lines.Add("    [JsonRequired]");
            else
            {
                type += "?";
                lines.Add("    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]");
            }
            lines.Add($"    public {(required ? "required " : "")}{type} {property} {{ get; init; }}");
            lines.Add("");
        }
        lines.Add("}");
        return string.Join("\n", lines);
    }

    internal Dictionary<string, string> Generate()
    {
        foreach (var root in _selection.GetProperty("roots").EnumerateObject())
        {
            var source = root.Value.GetProperty("source").GetString();
            JsonElement schema;
            if (source == "agentSandbox")
                schema = _sandbox;
            else if (source == "kubernetes")
            {
                var definition = root.Value.GetProperty("definition").GetString()!;
                if (!_definitions.TryGetValue(definition, out schema))
                    throw new InvalidOperationException($"Unknown root definition: {definition}");
            }
            else
                throw new InvalidOperationException($"Unknown schema source for {root.Name}");

            var paths = root.Value.GetProperty("fields").EnumerateArray().Select(value => value.GetString()!);
            var partial = root.Value.TryGetProperty("partial", out var partialValue)
                && partialValue.ValueKind == JsonValueKind.True;
            Select(schema, Tree(paths), root.Name, root.Name, partial);
        }

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in _models.Keys.Order(StringComparer.Ordinal))
            files.Add(name + ".g.cs", Header + ModelBody(name) + "\n");
        foreach (var name in _enums.Keys.Order(StringComparer.Ordinal))
            files.Add(name + ".g.cs", Header + EnumBody(name, _enums[name]) + "\n");

        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("schemaSha256");
            writer.WriteStartObject();
            foreach (var (name, hash) in _sourceHashes)
                writer.WriteString(name, hash);
            writer.WriteEndObject();
            writer.WriteNumber("generatedClasses", _models.Count);
            writer.WriteNumber("generatedEnums", _enums.Count);
            writer.WriteBoolean("usesIntOrString", _usesIntOrString);
            writer.WriteEndObject();
        }
        files.Add("manifest.json", Encoding.UTF8.GetString(output.ToArray()) + "\n");
        return files;
    }
}

internal sealed class SelectionNode
{
    internal readonly Dictionary<string, SelectionNode> Children = new(StringComparer.Ordinal);
}

internal sealed class SelectedModel
{
    internal readonly JsonElement Schema;
    internal readonly bool Partial;
    internal readonly Dictionary<string, SelectionNode> Fields = new(StringComparer.Ordinal);

    internal SelectedModel(JsonElement schema, bool partial)
    {
        Schema = schema;
        Partial = partial;
    }
}

internal static class KubernetesGeneratorTests
{
    private const string SandboxJson = """
        {"type":"object","required":["spec"],"properties":{"spec":{
          "type":"object","required":["name","unusedRequired"],"properties":{
            "name":{"type":"string"},"unusedRequired":{"type":"string"},
            "nested":{"type":"array","items":{"$ref":"#/definitions/io.k8s.Example"}}
          }}}}
        """;
    private const string DefinitionsJson = """
        {"io.k8s.Example":{"type":"object","properties":{
          "value":{"type":"integer","format":"int32"},"other":{"type":"string"}}}}
        """;

    internal static void Run()
    {
        using var sandbox = JsonDocument.Parse(SandboxJson);
        using var definitions = JsonDocument.Parse(DefinitionsJson);
        using var selection = JsonDocument.Parse("{}");
        var definitionMap = definitions.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);

        var selected = new KubernetesGenerator(selection.RootElement, sandbox.RootElement, definitionMap);
        selected.Select(sandbox.RootElement,
            KubernetesGenerator.Tree(["spec.name", "spec.nested.value"]), "Sandbox", "Sandbox", false);
        Check(selected.ModelNames.ToHashSet(StringComparer.Ordinal).SetEquals(["Sandbox", "SandboxSpec", "Example"]),
            "selection must include ancestors, array items, and referenced models");
        var root = selected.ModelBody("Sandbox");
        var spec = selected.ModelBody("SandboxSpec");
        var child = selected.ModelBody("Example");
        Check(root.Contains("public required SandboxSpec Spec", StringComparison.Ordinal), "required root field");
        Check(spec.Contains("[JsonPropertyName(\"nested\")]", StringComparison.Ordinal), "nested JSON name");
        Check(spec.Contains("public List<Example>? Nested", StringComparison.Ordinal), "array item type");
        Check(spec.Contains("public required string Name", StringComparison.Ordinal), "required selected field");
        Check(!spec.Contains("unusedRequired", StringComparison.Ordinal), "unselected required field");
        Check(child.Contains("public int? Value", StringComparison.Ordinal)
            && !child.Contains("Other", StringComparison.Ordinal), "selected reference field");

        using var overrideSelection = JsonDocument.Parse("""
            {"overrides":{"Sandbox.spec":"io.k8s.Example"}}
            """);
        var overridden = new KubernetesGenerator(overrideSelection.RootElement, sandbox.RootElement, definitionMap);
        overridden.Select(sandbox.RootElement,
            KubernetesGenerator.Tree(["spec.value"]), "Sandbox", "Sandbox", false);
        Check(overridden.ModelNames.ToHashSet(StringComparer.Ordinal).SetEquals(["Sandbox", "Example"])
            && overridden.ModelBody("Sandbox").Contains("public required Example Spec", StringComparison.Ordinal),
            "schema overrides must use the referenced type name");

        var unknown = new KubernetesGenerator(selection.RootElement, sandbox.RootElement, definitionMap);
        ExpectFailure(() => unknown.Select(sandbox.RootElement,
            KubernetesGenerator.Tree(["spec.missing"]), "Sandbox", "Sandbox", false),
            "Unknown selected field: Sandbox.spec.missing");

        var partial = new KubernetesGenerator(selection.RootElement, sandbox.RootElement, definitionMap);
        partial.Select(sandbox.RootElement,
            KubernetesGenerator.Tree(["spec.name"]), "SandboxPatch", "SandboxPatch", true);
        var patch = partial.ModelBody("SandboxPatch");
        var patchSpec = partial.ModelBody("SandboxPatchSpec");
        Check(patch.Contains("public SandboxPatchSpec? Spec", StringComparison.Ordinal)
            && patchSpec.Contains("public string? Name", StringComparison.Ordinal)
            && !patch.Contains("[JsonRequired]", StringComparison.Ordinal)
            && !patchSpec.Contains("[JsonRequired]", StringComparison.Ordinal), "partial patch fields must be optional");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("Generator test failed: " + message);
    }

    private static void ExpectFailure(Action action, string message)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException error) when (error.Message == message)
        {
            return;
        }
        throw new InvalidOperationException("Generator test failed: expected " + message);
    }
}
