import importlib.util
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


spec = importlib.util.spec_from_file_location(
    "generate_protocol", Path(__file__).with_name("generate-protocol.py")
)
protocol = importlib.util.module_from_spec(spec)
spec.loader.exec_module(protocol)


class ProtocolNamingTests(unittest.TestCase):
    def generate(self, definitions):
        with tempfile.TemporaryDirectory() as directory:
            schemas = Path(directory)
            (schemas / "codex_app_server_protocol.schemas.json").write_text(
                json.dumps({"definitions": definitions})
            )
            with patch.object(protocol, "SCHEMAS", schemas):
                files = protocol.Generator().run()
        return {name: text for name, text in files.items() if name.endswith(".g.cs")}

    def test_shared_words_are_removed_at_word_boundaries(self):
        for first, second, expected in [
            ("JSONRPCResponse", "JSONRPCMessage", "JSONRPCResponseMessage"),
            ("McpBooleanSchema", "McpPrimitiveSchema", "McpBooleanPrimitiveSchema"),
            ("Version2Event", "Version2Notice", "Version2EventNotice"),
            ("Foo", "Food", "FooFood"),
        ]:
            with self.subTest(first=first, second=second):
                self.assertEqual(expected, protocol.combine_type_names(first, second))

    def test_repeated_payload_name_is_shortened_without_changing_wire_names(self):
        owner = "AcceptWithExecpolicyAmendmentCommandExecutionApprovalDecision"
        files = self.generate(
            {
                owner: {
                    "type": "object",
                    "properties": {
                        "acceptWithExecpolicyAmendment": {
                            "type": "object",
                            "properties": {
                                "execpolicy_amendment": {
                                    "type": "array",
                                    "items": {"type": "string"},
                                }
                            },
                            "required": ["execpolicy_amendment"],
                        }
                    },
                    "required": ["acceptWithExecpolicyAmendment"],
                }
            }
        )
        self.assertEqual(
            {owner + ".g.cs", "AcceptWithExecpolicyAmendmentDetails.g.cs"}, set(files)
        )
        self.assertIn(
            '[JsonPropertyName("acceptWithExecpolicyAmendment")]',
            files[owner + ".g.cs"],
        )
        self.assertIn(
            '[JsonPropertyName("execpolicy_amendment")]',
            files["AcceptWithExecpolicyAmendmentDetails.g.cs"],
        )

    def test_generic_property_names_keep_their_owner_context(self):
        self.assertEqual(
            "GranularAskForApprovalGranular",
            protocol.nested_type_name(
                "GranularAskForApproval", "Granular", {"type": "object"}
            ),
        )

    def test_wrappers_do_not_take_their_payload_or_base_name(self):
        payload = "McpElicitationUntitledSingleSelectEnumSchema"
        self.assertEqual(
            payload + "Variant",
            protocol.variant_type_name(
                payload, "McpElicitationSingleSelectEnumSchema", wrapped=True
            ),
        )
        self.assertEqual(
            "ResultVariant", protocol.variant_type_name("Result", "Result")
        )

    def test_collisions_preserve_named_types_and_are_independent_of_definition_order(
        self,
    ):
        named = {"type": "object", "properties": {"original": {"type": "boolean"}}}
        definitions = {"AcceptWithPolicyDetails": named}
        for owner, value_type in [
            ("FirstAcceptWithPolicyDecision", "string"),
            ("SecondAcceptWithPolicyDecision", "integer"),
        ]:
            definitions[owner] = {
                "type": "object",
                "properties": {
                    "acceptWithPolicy": {
                        "type": "object",
                        "properties": {"value": {"type": value_type}},
                    }
                },
            }
        files = self.generate(definitions)
        self.assertEqual(
            files, self.generate(dict(reversed(list(definitions.items()))))
        )
        self.assertIn("public bool? Original", files["AcceptWithPolicyDetails.g.cs"])
        self.assertIn(
            "AcceptWithPolicyDetails2? AcceptWithPolicy",
            files["FirstAcceptWithPolicyDecision.g.cs"],
        )
        self.assertIn(
            "AcceptWithPolicyDetails3? AcceptWithPolicy",
            files["SecondAcceptWithPolicyDecision.g.cs"],
        )

    def test_identical_variants_with_different_bases_stay_distinct(self):
        variant = {
            "title": "SharedVariant",
            "type": "object",
            "properties": {"value": {"type": "string"}},
            "required": ["value"],
        }
        files = self.generate(
            {"FirstResult": {"oneOf": [variant]}, "SecondResult": {"oneOf": [variant]}}
        )
        self.assertIn("class SharedVariant : FirstResult", files["SharedVariant.g.cs"])
        self.assertIn(
            "class SharedVariant2 : SecondResult", files["SharedVariant2.g.cs"]
        )
        self.assertIn("Deserialize<SharedVariant>", files["FirstResult.g.cs"])
        self.assertIn("Deserialize<SharedVariant2>", files["SecondResult.g.cs"])


if __name__ == "__main__":
    unittest.main()
