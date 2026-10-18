"""Offline checks for context reconstruction and measurement boundaries. No credentials/network needed."""
import contextlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import urllib.error

import measure_tokens as m


def records(cid="test", fallback=False):
    data = []
    def add(event, **fields):
        data.append(dict(event=event, consultation_id=cid, sequence=len(data) + 1, **fields))
    usage = {k: 0 for k in m.USAGE}
    usage.update(total_input_tokens=100, total_output_tokens=5, total_tokens=105)
    first = {"model": "gemini-test", "input": [{"type": "user_input", "content": [{"type": "text", "text": '相談\n{"unknown_mod_value": "@a"}'}]}],
             "system_instruction": "System\n日本語", "tools": [{"type": "function", "name": "query_situation", "parameters": {"type": "object", "additionalProperties": False}}]}
    add("consultation_started", display_question="質問")
    add("request", api_request=1, key_slot="primary", body_json=json.dumps(first, ensure_ascii=False))
    call = {"type": "function_call", "id": "call-a", "name": "query_situation", "arguments": {"requests": [{"query": "get_units"}]}, "signature": "opaque-call"}
    add("response", api_request=1, http_status=200, body_complete=True, body_json=json.dumps({"id": "interaction-a", "usage": usage,
        "steps": [{"type": "thought", "signature": "opaque-thought"}, call]}))
    second = dict(first, previous_interaction_id="interaction-a", input=[{"type": "function_result", "call_id": "call-a", "name": "query_situation",
                  "result": [{"type": "text", "text": '{"name":"艦名","ammunition":80,"unknown":null}'}]}])
    if fallback:
        second = dict(first, model="gemini-sub-test", input=[{"type": "user_input", "content": [{"type": "text", "text": "Explicit continuation evidence"}]}])
    add("request", api_request=2, key_slot="fallback" if fallback else "primary", body_json=json.dumps(second))
    add("response", api_request=2, http_status=200, body_complete=True, body_json=json.dumps({"id": "interaction-b", "usage": usage,
        "steps": [{"type": "model_output", "content": [{"type": "text", "text": "回答"}]}]}))
    add("consultation_finished", outcome="completed", api_requests=2, query_rounds=1)
    return data


def raw(rows):
    return b"".join(m.encode(row).replace(b"\n", b"") + b"\r\n" for row in rows)


class MeasurementTests(unittest.TestCase):
    def test_context_includes_history_tools_and_exact_result_text(self):
        rows = records()
        prepared = m.prepare(rows)
        request = prepared[1]["count_request"]["generateContentRequest"]
        contents = request["contents"]
        self.assertEqual([v["role"] for v in contents], ["user", "model", "user"])
        self.assertEqual(contents[0]["parts"][0]["text"], json.loads(rows[1]["body_json"])["input"][0]["content"][0]["text"])
        self.assertEqual(contents[1]["parts"][0]["functionCall"]["id"], "call-a")
        self.assertEqual(contents[2]["parts"][0]["functionResponse"]["response"]["result"], json.loads(rows[3]["body_json"])["input"][0]["result"][0]["text"])
        self.assertEqual(request["systemInstruction"]["parts"][0]["text"], "System\n日本語")
        self.assertFalse(request["tools"][0]["functionDeclarations"][0]["parametersJsonSchema"]["additionalProperties"])
        self.assertEqual(prepared[1]["omitted_context"], {"thought_steps": 1, "opaque_signatures": 2})
        self.assertNotIn("opaque-call", json.dumps(request))
        self.assertEqual(len(prepared[0]["count_request"]["generateContentRequest"]["contents"]), 1)

    def test_missing_history_or_unsupported_content_fails_instead_of_undercounting(self):
        rows = records()
        wire = json.loads(rows[3]["body_json"]); wire["previous_interaction_id"] = "unavailable"
        rows[3]["body_json"] = json.dumps(wire)
        with self.assertRaisesRegex(m.MeasurementError, "Cannot resolve"):
            m.prepare(rows)
        rows = records(); wire = json.loads(rows[1]["body_json"])
        wire["input"][0]["content"][0] = {"type": "image", "data": "unknown"}
        rows[1]["body_json"] = json.dumps(wire)
        with self.assertRaisesRegex(m.MeasurementError, "non-text"):
            m.prepare(rows)
        with self.assertRaisesRegex(m.MeasurementError, "unfinished"):
            m.select_consultation(raw(records()[:-1]))

    def test_sub_connection_starts_a_new_context(self):
        prepared = m.prepare(records(fallback=True))
        second = prepared[1]
        self.assertEqual(second["model"], "gemini-sub-test")
        self.assertEqual(second["key_slot"], "fallback")
        self.assertEqual(second["omitted_context"]["thought_steps"], 0)
        self.assertEqual(second["count_request"]["generateContentRequest"]["contents"], [{"role": "user", "parts": [{"text": "Explicit continuation evidence"}]}])

    def test_capture_is_offline_immutable_and_does_not_select_connection_checks(self):
        with tempfile.TemporaryDirectory() as folder:
            folder = Path(folder); source = folder / "source.jsonl"; output = folder / "capture"
            selected = raw(records()); checks = raw(records("connection-test-1"))
            source.write_bytes(selected + checks)
            with patch.object(m, "count_tokens", side_effect=AssertionError("Unexpected network call")):
                result = m.capture(source, output)
            self.assertEqual(result["consultation_id"], "test")
            self.assertEqual(source.read_bytes(), selected + checks)
            self.assertEqual((output / "ai-communication.jsonl").read_bytes(), selected)
            self.assertIsNone(result["totals"]["count_tokens"])
            with self.assertRaisesRegex(m.MeasurementError, "already exists"):
                m.capture(source, output)

    def test_measure_sends_count_requests_and_resumes_without_recounting(self):
        with tempfile.TemporaryDirectory() as folder:
            folder = Path(folder); source = folder / "source.jsonl"; source.write_bytes(raw(records()))
            output = folder / "capture"; m.capture(source, output)
            seen = []
            class Opener:
                def open(self, req, timeout):
                    seen.append(req)
                    self_outer.assertEqual(req.get_header("X-goog-api-key"), "mock-secret")
                    self_outer.assertNotIn("mock-secret", req.full_url)
                    self_outer.assertTrue(req.full_url.endswith("/models/gemini-test:countTokens"))
                    payload = json.loads(req.data)
                    self_outer.assertIn("systemInstruction", payload["generateContentRequest"])
                    return contextlib.closing(io.BytesIO(b'{"totalTokens":123}'))
            self_outer = self
            with patch.object(m.urllib.request, "build_opener", return_value=Opener()):
                report, failed = m.measure(output, {"primary": "mock-secret"})
                m.measure(output, {})
            self.assertFalse(failed); self.assertEqual(len(seen), 2)
            self.assertEqual(report["totals"]["count_tokens"], 246)
            self.assertEqual(report["totals"]["api_reported"]["total_input_tokens"], 200)
            self.assertNotIn("mock-secret", (output / "measurement.json").read_text())
            (output / "count-requests/001.json").write_text("{}")
            with patch.object(m, "count_tokens", side_effect=AssertionError("Unexpected network call")):
                with self.assertRaisesRegex(m.MeasurementError, "changed"):
                    m.measure(output, {})

    def test_missing_usage_and_count_failure_remain_unknown_and_secrets_are_not_written(self):
        with tempfile.TemporaryDirectory() as folder:
            folder = Path(folder); source = folder / "source.jsonl"; rows = records()
            reply = json.loads(rows[4]["body_json"]); reply.pop("usage"); rows[4]["body_json"] = json.dumps(reply)
            source.write_bytes(raw(rows)); output = folder / "capture"; m.capture(source, output)
            with patch.object(m.urllib.request, "build_opener") as opener:
                opener.return_value.open.side_effect = urllib.error.HTTPError("https://generativelanguage.googleapis.com/", 429, "mock-secret echoed", {}, io.BytesIO(b"mock-secret"))
                report, failed = m.measure(output, {"primary": "mock-secret"})
            self.assertTrue(failed); self.assertIsNone(report["totals"]["count_tokens"])
            self.assertIsNone(report["totals"]["api_reported"]["total_input_tokens"])
            self.assertEqual(report["requests"][0]["count_error"], "countTokens HTTP 429")
            self.assertNotIn("mock-secret", (output / "measurement.json").read_text())
            with self.assertRaisesRegex(m.MeasurementError, "Invalid API credential"):
                m.count_tokens("gemini-test", {}, "秘密のキー", 1)

    def test_comparison_distinguishes_unknown_from_zero_and_reports_changed_requests(self):
        with tempfile.TemporaryDirectory() as folder:
            folder = Path(folder); source = folder / "source.jsonl"; source.write_bytes(raw(records()))
            a = m.capture(source, folder / "a"); b = m.capture(source, folder / "b")
            a["requests"][0]["count_tokens"] = 0; m.summarize(a)
            b["requests"][0]["retrieval_requests"] = []
            comparison = m.compare(a, b)
            self.assertIn("| countTokens reconstructed input | — | — | — |", comparison)
            self.assertIn("Same retrieval requests: False", comparison)
            b["method"] = "different"
            with self.assertRaises(m.MeasurementError):
                m.compare(a, b)

    def test_credentials_support_current_and_legacy_local_settings_without_copying_them(self):
        with tempfile.TemporaryDirectory() as folder, patch.dict(m.os.environ, {}, clear=True):
            path = Path(folder) / "settings.json"
            path.write_bytes(m.encode({"schema_version": 2, "api_configurations": [{"id": "a", "api_key": "mock-a"}, {"id": "b", "api_key": "mock-b"}], "main_api_id": "b", "sub_api_id": "a"}))
            self.assertEqual(m.credentials(path), {"primary": "mock-b", "fallback": "mock-a"})
            path.write_bytes(m.encode({"api_key": "mock-old", "fallback_api_key": ""}))
            self.assertEqual(m.credentials(path), {"primary": "mock-old", "fallback": ""})


if __name__ == "__main__":
    unittest.main()
