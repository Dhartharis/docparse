"""Check the proposed wire contract offline; this does not test a running provider."""

from copy import deepcopy
from datetime import datetime
import hashlib
import hmac
import json
from pathlib import Path
import re
import unittest

from jsonschema import Draft202012Validator, FormatChecker


ROOT = Path(__file__).resolve().parents[1]
CONTRACT = ROOT / "contracts" / "operational-data" / "v1"
SCHEMA = json.loads((CONTRACT / "schema.json").read_text(encoding="utf-8"))
OPENAPI = json.loads((CONTRACT / "openapi.json").read_text(encoding="utf-8"))
FORMATS = FormatChecker()


@FORMATS.checks("date-time", raises=ValueError)
def is_utc_instant(value):
    """Do not let an optional RFC3339 checker dependency silently disable dates."""
    if not isinstance(value, str):
        return True
    if not re.fullmatch(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?Z", value):
        return False
    datetime.fromisoformat(value.replace("Z", "+00:00"))
    return True


def example(name):
    return json.loads((CONTRACT / "examples" / name).read_text(encoding="utf-8"))


def validator(name):
    # All references stay inside the published schema; never fetch remote schemas.
    schema = {"$schema": SCHEMA["$schema"], "$defs": SCHEMA["$defs"], "$ref": f"#/$defs/{name}"}
    return Draft202012Validator(schema, format_checker=FORMATS)


def all_refs(value):
    if isinstance(value, dict):
        if "$ref" in value:
            yield value["$ref"]
        for child in value.values():
            yield from all_refs(child)
    elif isinstance(value, list):
        for child in value:
            yield from all_refs(child)


class OperationalContractTests(unittest.TestCase):
    def setUp(self):
        self.event = example("push-upsert.json")
        self.offer = self.event["change"]["offer"]

    def test_schema_is_valid_and_all_references_resolve_locally(self):
        Draft202012Validator.check_schema(SCHEMA)
        for document in (SCHEMA, OPENAPI):
            for reference in all_refs(document):
                with self.subTest(reference=reference):
                    path, pointer = reference.split("#", 1)
                    self.assertIn(path, ("", "./schema.json"))
                    target = SCHEMA if path else document
                    for segment in pointer.lstrip("/").split("/"):
                        target = target[segment.replace("~1", "/").replace("~0", "~")]

    def test_every_published_example_matches_its_wire_schema(self):
        examples = {
            "push-upsert.json": "PushEvent", "push-delete.json": "PushEvent",
            "query-request.json": "QueryRequest", "query-unavailable.json": "QueryResponse",
            "changes-empty-complete.json": "ChangesPage", "receipt.json": "Receipt",
            "cursor-expired.json": "Problem",
        }
        self.assertEqual(set(examples), {p.name for p in (CONTRACT / "examples").glob("*.json")})
        for filename, schema in examples.items():
            with self.subTest(filename=filename):
                validator(schema).validate(example(filename))

    def test_operations_declare_authentication_headers_and_path_parameters(self):
        self.assertEqual(OPENAPI["security"], [{"SignedRequest": []}])
        operations = set()
        for path, item in OPENAPI["paths"].items():
            parameters = []
            for parameter in item.get("parameters", []):
                if "$ref" in parameter:
                    parameter = OPENAPI["components"]["parameters"][parameter["$ref"].rsplit("/", 1)[1]]
                parameters.append(parameter)
            for name in ("X-Fyr-Key-Id", "X-Fyr-Timestamp", "X-Fyr-Request-Id"):
                self.assertTrue(any(p["name"] == name and p["in"] == "header" and p["required"] for p in parameters))
            self.assertEqual(set(re.findall(r"\{([^}]+)\}", path)), {p["name"] for p in parameters if p["in"] == "path" and p["required"]})
            for method in ("get", "post"):
                if method not in item:
                    continue
                operation = item[method]
                self.assertNotIn(operation["operationId"], operations)
                operations.add(operation["operationId"])
                if method == "get":
                    self.assertNotIn("requestBody", operation)
                else:
                    self.assertTrue(operation["requestBody"]["required"])
                self.assertIn("401", operation["responses"])
                self.assertIn("403", operation["responses"])
                self.assertNotIn("security", operation)  # No anonymous override.
        self.assertEqual(len(operations), 4)

    def test_money_and_quantities_are_exact_non_negative_strings(self):
        for bad in (4.99, -1, "-1", "+1", "1e3", "NaN", "Infinity", "01", "1,000", "0.1234567", "1" * 19):
            with self.subTest(value=bad):
                value = deepcopy(self.offer)
                value["price"]["amount"] = bad
                self.assertFalse(validator("Offer").is_valid(value))
                value = deepcopy(self.offer)
                value["availability"]["available"]["quantity"] = bad
                self.assertFalse(validator("Offer").is_valid(value))
        value = deepcopy(self.offer)
        value["price"]["amount"] = "0"
        value["availability"]["available"]["quantity"] = "0.000001"
        validator("Offer").validate(value)

    def test_unknown_price_and_quantity_cannot_smuggle_confirmed_values(self):
        self.offer["price"] = {"state": "unknown", "reason": "not_provided"}
        self.offer["availability"]["available"] = {"state": "unknown", "reason": "source_unavailable"}
        validator("Offer").validate(self.offer)
        self.offer["price"]["amount"] = "0"
        self.assertFalse(validator("Offer").is_valid(self.offer))

    def test_untracked_inventory_never_reports_known_stock(self):
        self.offer["availability"]["tracking"] = "untracked"
        self.assertFalse(validator("Offer").is_valid(self.offer))
        for field in ("available", "reserved"):
            self.offer["availability"][field] = {"state": "unknown", "reason": "not_tracked"}
        validator("Offer").validate(self.offer)

    def test_revision_above_javascript_safe_integer_is_preserved_as_text(self):
        self.assertEqual(self.offer["version"], "9007199254740993")
        self.assertGreater(int(self.offer["version"]), 2**53)
        validator("Offer").validate(self.offer)
        for bad in (9007199254740993, "0", "01", "-1", "1.0", "1e10", "9" * 39):
            with self.subTest(version=bad):
                self.offer["version"] = bad
                self.assertFalse(validator("Offer").is_valid(self.offer))

    def test_dates_require_real_utc_calendar_instants(self):
        for bad in ("2026-02-30T00:00:00Z", "2026-11-11", "2026-11-11T00:00:00+00:00", "2026-11-11T25:00:00Z"):
            with self.subTest(date=bad):
                self.event["occurredAt"] = bad
                self.assertFalse(validator("PushEvent").is_valid(self.event))

    def test_tenant_and_mutation_fields_are_not_accepted_as_offer_data(self):
        for field in ("organizationId", "sourceBusinessId", "reserveQuantity", "executeUrl"):
            with self.subTest(field=field):
                value = deepcopy(self.offer)
                value[field] = "untrusted"
                self.assertFalse(validator("Offer").is_valid(value))

    def test_push_union_rejects_delta_mixed_and_unknown_events(self):
        for change in ({"kind": "delta", "quantity": "-1"}, {**self.event["change"], "tombstone": example("push-delete.json")["change"]["tombstone"]}):
            self.event["change"] = change
            self.assertFalse(validator("PushEvent").is_valid(self.event))
        self.event = example("push-upsert.json")
        self.event["contractVersion"] = "2"
        self.assertFalse(validator("PushEvent").is_valid(self.event))

    def test_query_selector_is_exclusive_bounded_and_fields_are_explicit(self):
        request = example("query-request.json")
        for selector in ({}, {"text": "coffee", "skus": ["COFFEE"]}, {"externalIds": ["same", "same"]}, {"skus": [f"sku-{n}" for n in range(51)]}):
            with self.subTest(selector=selector):
                request["selector"] = selector
                self.assertFalse(validator("QueryRequest").is_valid(request))
        request = example("query-request.json")
        request["fields"] = ["catalog", "execute_sale"]
        self.assertFalse(validator("QueryRequest").is_valid(request))

    def test_found_ambiguous_and_missing_results_cannot_be_confused(self):
        result = {"selectorValue": "COFFEE", "status": "found", "offers": [self.offer]}
        validator("QueryResult").validate(result)
        for status, offers in (("found", []), ("ambiguous", [self.offer]), ("not_found", [self.offer]), ("unavailable", [self.offer])):
            with self.subTest(status=status):
                result.update(status=status, offers=offers)
                self.assertFalse(validator("QueryResult").is_valid(result))

    def test_snapshot_needs_a_watermark_and_never_a_null_resume_cursor(self):
        page = example("changes-empty-complete.json")
        for field in ("snapshotId", "snapshotWatermark", "nextCursor"):
            with self.subTest(field=field):
                invalid = {**page, field: None}
                self.assertFalse(validator("ChangesPage").is_valid(invalid))
        page["changes"] = [example("push-delete.json")["change"]]
        self.assertFalse(validator("ChangesPage").is_valid(page))
        page.update(mode="incremental", snapshotId=None, snapshotWatermark=None)
        validator("ChangesPage").validate(page)

    def test_receipt_failure_has_an_explicit_error_and_success_has_none(self):
        receipt = example("receipt.json")
        receipt["status"] = "failed"
        self.assertFalse(validator("Receipt").is_valid(receipt))
        receipt["errorCode"] = "record_version_conflict"
        validator("Receipt").validate(receipt)
        receipt["status"] = "applied"
        self.assertFalse(validator("Receipt").is_valid(receipt))

    def test_authentication_vectors_preserve_raw_bytes_and_request_target(self):
        vectors = json.loads((CONTRACT / "authentication-vectors.json").read_text(encoding="utf-8"))
        for vector in vectors["vectors"]:
            with self.subTest(vector=vector["id"]):
                body = vector["bodyUtf8"].encode("utf-8")
                digest = hashlib.sha256(body).hexdigest()
                canonical = "\n".join(("FYR-OPDATA-V1", vector["method"], vector["target"], vector["keyId"], vector["timestamp"], vector["requestId"], digest))
                self.assertEqual(vector["expectedBodySha256"], digest)
                self.assertEqual(vector["expectedCanonical"], canonical)
                signature = "v1=" + hmac.new(bytes.fromhex(vector["testKeyHex"]), canonical.encode("utf-8"), hashlib.sha256).hexdigest()
                self.assertEqual(vector["expectedSignature"], signature)
                # Raw body bytes, target ordering/escaping and trailing LF are authenticated.
                for changed in (canonical + "\n", canonical.replace(vector["target"], vector["target"] + "&tampered=1", 1), canonical.replace(digest, hashlib.sha256(body + b" ").hexdigest())):
                    altered = "v1=" + hmac.new(bytes.fromhex(vector["testKeyHex"]), changed.encode("utf-8"), hashlib.sha256).hexdigest()
                    self.assertNotEqual(signature, altered)


if __name__ == "__main__":
    unittest.main(verbosity=2)
