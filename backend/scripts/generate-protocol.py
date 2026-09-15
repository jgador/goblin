#!/usr/bin/env python3
"""Generate explicit System.Text.Json POCOs from the checked-in Codex contract.

No network access or third-party Python packages are used. Run with --check in CI.
Unsupported schema constructs fail generation instead of falling back to object.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SCHEMAS = ROOT / "schemas" / "codex"
OUTPUT = ROOT / "src" / "Goblin.Protocol" / "Generated"
METADATA = {"$schema", "title", "description", "default"}


def semantic(value):
    if isinstance(value, dict):
        return {
            key: item.rsplit("/", 1)[-1] if key == "$ref" else semantic(item)
            for key, item in sorted(value.items())
            if key not in METADATA
        }
    if isinstance(value, list):
        return [semantic(item) for item in value]
    return value


def pascal(value):
    value = re.sub(r"v\d+::", "", value)
    parts = re.split(r"[^a-zA-Z0-9]+", value)
    result = "".join(part[0].upper() + part[1:] if not part.isupper() else part.title()
                     for part in parts if part)
    return ("Value" + result) if not result or result[0].isdigit() else result


def literal(value):
    return json.dumps(value, ensure_ascii=True)


def unwrap(schema):
    if not isinstance(schema, dict):
        return schema
    if "allOf" in schema:
        if len(schema["allOf"]) != 1:
            raise ValueError("Unsupported multi-branch allOf")
        return unwrap(schema["allOf"][0])
    return schema


def nullable_schema(schema):
    schema = unwrap(schema)
    if schema is True or isinstance(schema, dict) and not semantic(schema):
        return False, schema
    if isinstance(schema.get("type"), list):
        types = schema["type"]
        if len(types) != 2 or "null" not in types:
            raise ValueError(f"Unsupported type union: {types}")
        return True, {**schema, "type": next(t for t in types if t != "null")}
    for key in ("anyOf", "oneOf"):
        if key in schema and len(schema[key]) == 2:
            nonnull = [branch for branch in schema[key] if branch.get("type") != "null"]
            if len(nonnull) == 1:
                return True, nonnull[0]
    return False, schema


class Generator:
    def __init__(self):
        aggregate = json.loads((SCHEMAS / "codex_app_server_protocol.schemas.json").read_text())
        self.schemas = {}
        self.refs = {}
        for key, schema in aggregate["definitions"].items():
            definitions = schema.items() if key == "v2" else [(key, schema)]
            for name, value in definitions:
                ref = f"#/definitions/{'v2/' if key == 'v2' else ''}{name}"
                if name in self.schemas and semantic(value) != semantic(self.schemas[name]):
                    raise ValueError(f"Conflicting protocol definition: {name}")
                self.schemas[name] = value
                self.refs[ref] = name
        self.named = set(self.schemas)
        self.verify_individual_schemas()
        self.generated = {}
        self.generating = set()
        self.aliases = {}
        self.source_hash = self.hash_schemas()

    def verify_individual_schemas(self):
        # Both aggregate and per-message schemas are checked-in contract inputs.
        # Refuse to silently prefer one if a future refresh leaves them inconsistent.
        for path in sorted(SCHEMAS.rglob("*.json")):
            if path.name == "codex_app_server_protocol.schemas.json":
                continue
            schema = json.loads(path.read_text())
            if path.name == "codex_app_server_protocol.v2.schemas.json":
                local = schema["definitions"]
            else:
                local = {path.stem: {key: value for key, value in schema.items() if key != "definitions"}}
                local.update(schema.get("definitions", {}))
            for name, value in local.items():
                if name not in self.schemas or semantic(value) != semantic(self.schemas[name]):
                    raise ValueError(f"Aggregate and individual schemas disagree: {path.relative_to(ROOT)}: {name}")

    @staticmethod
    def hash_schemas():
        digest = hashlib.sha256()
        for path in sorted(SCHEMAS.rglob("*.json")):
            digest.update(path.relative_to(SCHEMAS).as_posix().encode())
            digest.update(b"\0")
            digest.update(path.read_bytes())
            digest.update(b"\0")
        return digest.hexdigest()

    def register(self, name, schema):
        name = pascal(name)
        if name in self.schemas:
            if semantic(self.schemas[name]) == semantic(schema):
                return name
            suffix = 2
            while name + str(suffix) in self.schemas:
                if semantic(self.schemas[name + str(suffix)]) == semantic(schema):
                    return name + str(suffix)
                suffix += 1
            name += str(suffix)
        self.schemas[name] = schema
        return name

    def enum_values(self, schema):
        schema = unwrap(schema)
        if "enum" in schema and schema.get("type") == "string":
            return schema["enum"]
        branches = schema.get("oneOf", schema.get("anyOf"))
        if branches and all(branch.get("type") == "string" and "enum" in branch for branch in branches):
            return [item for branch in branches for item in branch["enum"]]
        return None

    def type_name(self, schema, hint):
        nullable, schema = nullable_schema(schema)
        if schema is True or isinstance(schema, dict) and not semantic(schema):
            name = "JsonElement"
        elif not isinstance(schema, dict):
            raise ValueError(f"Unsupported schema at {hint}: {schema}")
        elif "$ref" in schema:
            ref = schema["$ref"]
            if ref not in self.refs:
                raise ValueError(f"Unresolved reference at {hint}: {ref}")
            name = self.emit(self.refs[ref])
        elif self.enum_values(schema) is not None or "oneOf" in schema or "anyOf" in schema:
            name = self.emit(self.register(hint, schema))
        elif schema.get("type") == "string":
            name = "string"
        elif schema.get("type") == "boolean":
            name = "bool"
        elif schema.get("type") == "null":
            name = "ProtocolNull"
        elif schema.get("type") == "integer":
            name = {"int64": "long", "int32": "int", "uint64": "ulong", "uint32": "uint",
                    "uint16": "ushort", "uint": "ulong", None: "long"}.get(schema.get("format"))
            if name is None:
                raise ValueError(f"Unknown integer format at {hint}")
        elif schema.get("type") == "number":
            name = "double"
        elif schema.get("type") == "array":
            name = f"List<{self.type_name(schema['items'], hint + 'Item')}>"
        elif schema.get("type") == "object":
            if "properties" not in schema and schema.get("additionalProperties") not in (None, False):
                name = f"Dictionary<string, {self.type_name(schema['additionalProperties'], hint + 'Value')}>"
            else:
                name = self.emit(self.register(hint, schema))
        else:
            raise ValueError(f"Unsupported schema at {hint}: {schema}")
        return name + "?" if nullable and not name.endswith("?") else name

    def emit(self, name):
        if name in self.aliases:
            return self.aliases[name]
        if name in self.generated or name in self.generating:
            return name
        nullable, schema = nullable_schema(self.schemas[name])
        self.generating.add(name)
        if nullable:
            alias = self.type_name(schema, name + "Value")
            self.aliases[name] = alias if alias.endswith("?") else alias + "?"
            self.generating.remove(name)
            return self.aliases[name]
        if name == "RequestId":
            expected = {"anyOf": [{"type": "string"}, {"format": "int64", "type": "integer"}]}
            if semantic(schema) != expected:
                raise ValueError("RequestId schema changed; update its scalar union representation")
            body = self.request_id()
        elif self.enum_values(schema) is not None:
            body = self.enum(name, self.enum_values(schema))
        elif "oneOf" in schema or "anyOf" in schema:
            body = self.union(name, schema)
        elif schema.get("type") == "object":
            body = self.object(name, schema)
        else:
            alias = self.type_name(schema, name)
            self.aliases[name] = alias
            self.generating.remove(name)
            return alias
        self.generated[name] = body
        self.generating.remove(name)
        return name

    @staticmethod
    def enum(name, values):
        if len(values) != len(set(values)):
            raise ValueError(f"Duplicate enum variants in {name}")
        names = set()
        lines = [f"[JsonConverter(typeof(ProtocolStringEnumConverter<{name}>))]", f"public enum {name}", "{"]
        for value in values:
            member = pascal(value)
            if member in names:
                raise ValueError(f"Enum member collision in {name}: {value}")
            names.add(member)
            lines += [f"    [JsonStringEnumMemberName({literal(value)})]", f"    {member},"]
        return "\n".join(lines + ["}"])

    def properties(self, name, schema, discriminator=None, inherited=False):
        lines = []
        for key, value in schema.get("properties", {}).items():
            prop = pascal(key)
            if prop == name:
                prop += "Value"
            required = key in schema.get("required", [])
            lines.append(f"    [JsonPropertyName({literal(key)})]")
            if discriminator == key:
                constant = value["enum"][0]
                lines += ["    [JsonRequired]",
                          f"    public {'override ' if inherited else ''}string {prop}", "    {",
                          f"        get => {literal(constant)};", "        init",
                          "        {", f"            if (value != {literal(constant)})",
                          f"                throw new JsonException({literal('Expected ' + key + ' discriminator ' + constant + '.')});",
                          "        }", "    }", ""]
                continue
            type_name = self.type_name(value, name + prop)
            if required:
                lines.append("    [JsonRequired]")
            else:
                if not type_name.endswith("?"):
                    type_name += "?"
                lines.append("    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]")
            lines += [f"    public {'required ' if required else ''}{type_name} {prop} {{ get; init; }}", ""]
        if schema.get("additionalProperties") is True:
            lines += ["    [JsonExtensionData]", "    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }", ""]
        return lines

    def object(self, name, schema, parent=None, discriminator=None):
        lines = []
        if schema.get("additionalProperties") is False:
            lines.append("[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]")
        lines += [f"public sealed record {name}" + (f" : {parent}" if parent else ""), "{"]
        lines += self.properties(name, schema, discriminator, parent is not None)
        return "\n".join(lines + ["}"])

    def discriminator(self, branches):
        for key in ("type", "method", "kind", "mode", "handlerType"):
            values = []
            for branch in branches:
                prop = branch.get("properties", {}).get(key, {})
                if len(prop.get("enum", [])) != 1 or key not in branch.get("required", []):
                    break
                values.append(prop["enum"][0])
            if len(values) == len(branches) and len(set(values)) == len(values):
                return key
        return None

    def union(self, name, schema):
        branches = schema.get("oneOf", schema.get("anyOf"))
        discriminator = self.discriminator(branches)
        lines = [f"[JsonConverter(typeof({name}JsonConverter))]", f"public abstract record {name}", "{"]
        if discriminator:
            lines += [f"    [JsonPropertyName({literal(discriminator)})]",
                      f"    public abstract string {pascal(discriminator)} {{ get; init; }}", ""]
            if discriminator == "method":
                methods = [branch["properties"]["method"]["enum"][0] for branch in branches]
                lines += ["    public static bool IsKnownMethod(string method) => method is",
                          "\n".join(f"        {literal(method)}" + (" or" if index < len(methods) - 1 else ";")
                                    for index, method in enumerate(methods)), ""]
        lines += self.properties(name, schema)
        variants = []
        for index, branch in enumerate(branches):
            if discriminator:
                value = branch["properties"][discriminator]["enum"][0]
                variant = self.register(pascal(value) + name, branch)
                self.generated[variant] = self.object(variant, branch, name, discriminator)
                variants.append((variant, variant, False, value))
            elif branch.get("type") == "object":
                rawname = branch.get("title")
                if not rawname:
                    required = branch.get("required", [])
                    rawname = (pascal(required[0]) if required else f"Variant{index + 1}") + name
                variant = self.register(rawname, branch)
                self.generated[variant] = self.object(variant, branch, name)
                variants.append((variant, variant, False, None))
            else:
                enum_values = self.enum_values(branch)
                inner = self.type_name(branch, name + ("Value" if enum_values else f"Variant{index + 1}Value"))
                label = "String" if enum_values or inner == "string" else "Array" if inner.startswith("List<") else pascal(inner)
                variant = self.register(label + name, {"type": "object", "properties": {"value": branch}, "required": ["value"]})
                self.generated[variant] = (f"[JsonConverter(typeof(ProtocolValueConverter<{variant}, {inner}>))]\n"
                                           f"public sealed record {variant}({inner} Value) : {name}, IProtocolValue<{variant}, {inner}>\n"
                                           "{\n"
                                           f"    public static {variant} FromValue({inner} value) => new(value);\n"
                                           "}")
                variants.append((variant, inner, True, None))
                if enum_values:
                    for value in enum_values:
                        lines.append(f"    public static {name} {pascal(value)} {{ get; }} = new {variant}({inner}.{pascal(value)});")
        lines += ["}", "", f"public sealed class {name}JsonConverter : JsonConverter<{name}>", "{",
                  f"    public override {name} Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)", "    {"]
        if discriminator:
            lines += [f"        return ProtocolUnion.ReadDiscriminator(ref reader, {literal(discriminator)}) switch", "        {"]
            for variant, _, _, value in variants:
                lines.append(f"            {literal(value)} => JsonSerializer.Deserialize<{variant}>(ref reader, options)!,")
            lines += [f"            _ => throw new JsonException({literal('Unknown ' + name + ' discriminator.')}),", "        };"]
        else:
            for index, (variant, inner, wrapped, _) in enumerate(variants):
                lines += ["        {", "            var candidate = reader;", "            try", "            {",
                          f"                var value = JsonSerializer.Deserialize<{inner}>(ref candidate, options);",
                          "                if (value is null) throw new JsonException(\"Expected a non-null union value.\");" if inner not in ("long", "int", "bool", "ulong", "uint", "ushort", "double", "ProtocolNull") and self.enum_values(self.schemas.get(inner, {})) is None else "",
                          "                reader = candidate;",
                          f"                return {'new ' + variant + '(value)' if wrapped else 'value'};",
                          "            }", "            catch (JsonException)", "            {", "                // This branch does not match; try the next schema alternative.", "            }", "        }"]
            lines.append(f"        throw new JsonException({literal('Value does not match any ' + name + ' schema alternative.')});")
        lines += ["    }", "", f"    public override void Write(Utf8JsonWriter writer, {name} value, JsonSerializerOptions options)", "    {", "        switch (value)", "        {"]
        for variant, _, wrapped, _ in variants:
            lines += [f"            case {variant} typed:", f"                JsonSerializer.Serialize(writer, typed{'.Value' if wrapped else ''}, options);", "                break;"]
        lines += ["            default:", f"                throw new JsonException({literal('Unknown ' + name + ' implementation.')});", "        }", "    }", "}"]
        return "\n".join(line for line in lines if line is not None)

    @staticmethod
    def request_id():
        return '''[JsonConverter(typeof(RequestIdJsonConverter))]
public readonly record struct RequestId
{
    public RequestId(string value) => String = value ?? throw new ArgumentNullException(nameof(value));
    public RequestId(long value) => Number = value;

    public string? String { get; }
    public long? Number { get; }

    public static implicit operator RequestId(string value) => new(value);
    public static implicit operator RequestId(long value) => new(value);
    public override string ToString() => String ?? Number?.ToString(System.Globalization.CultureInfo.InvariantCulture)
        ?? throw new InvalidOperationException("An uninitialized request ID has no wire representation.");
}

public sealed class RequestIdJsonConverter : JsonConverter<RequestId>
{
    public override RequestId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => new RequestId(reader.GetString()!),
            JsonTokenType.Number when reader.TryGetInt64(out var number) => new RequestId(number),
            _ => throw new JsonException("A request ID must be a string or signed 64-bit integer."),
        };

    public override void Write(Utf8JsonWriter writer, RequestId value, JsonSerializerOptions options)
    {
        if (value.String is { } text) writer.WriteStringValue(text);
        else if (value.Number is { } number) writer.WriteNumberValue(number);
        else throw new JsonException("An uninitialized request ID has no wire representation.");
    }
}'''

    def run(self):
        for name in sorted(self.named):
            self.emit(name)
        header = "// <auto-generated />\n// Source: backend/schemas/codex; regenerate with python3 backend/scripts/generate-protocol.py\n#nullable enable\nusing System;\nusing System.Collections.Generic;\nusing System.Text.Json;\nusing System.Text.Json.Serialization;\n\nnamespace Goblin.Protocol;\n\n"
        files = {name + ".g.cs": header + value + "\n" for name, value in sorted(self.generated.items())}
        manifest = {"schemaSha256": self.source_hash, "schemaFiles": len(list(SCHEMAS.rglob('*.json'))),
                    "namedDefinitions": len(self.named), "generatedTypes": len(self.generated),
                    "primitiveAliases": dict(sorted(self.aliases.items()))}
        files["manifest.json"] = json.dumps(manifest, indent=2) + "\n"
        return files


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="Fail when generated files differ or schemas have changed")
    args = parser.parse_args()
    files = Generator().run()
    current = {p.name: p for p in OUTPUT.glob("*") if p.is_file()}
    changed = sorted(name for name, content in files.items() if name not in current or current[name].read_text() != content)
    stale = sorted(set(current) - set(files))
    if args.check:
        if changed or stale:
            print("Protocol generation is out of date: " + ", ".join(changed + stale), file=sys.stderr)
            return 1
        print(f"Protocol generation is current ({len(files) - 1} generated model files).")
        return 0
    OUTPUT.mkdir(parents=True, exist_ok=True)
    for name in stale:
        current[name].unlink()
    for name, content in files.items():
        (OUTPUT / name).write_text(content)
    print(f"Generated {len(files) - 1} model files from backend/schemas/codex.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
