"""CLI protocol/workflow tests using synthetic data and local mock HTTP only."""
import base64
import contextlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import threading
import unittest
from unittest.mock import patch

import advisor_dev as tool


class MockApi:
    def __init__(self):
        self.calls = []
        self.fields = {"common.json:system": "Original\n日本語", "requests.json:key_risks": "Original risk"}
        self.states = ["starting", "waiting", "answered"]
        self.busy = False
        self.fail_post = None
        self.disconnect_post = False
        self.answer = "**回答**\n艦名と\"引用\"、`$()`、𠮷\n"
        self.server = None

    def __enter__(self):
        api = self
        class Handler(BaseHTTPRequestHandler):
            def do_GET(self): self.handle_api()
            def do_POST(self): self.handle_api()
            def do_PATCH(self): self.handle_api()
            def log_message(self, *args): pass
            def handle_api(self):
                raw = self.rfile.read(int(self.headers.get("Content-Length", 0)))
                body = json.loads(raw) if raw else None
                api.calls.append((self.command, self.path, body, self.headers.get("X-Advisor-Developer")))
                status = 200
                if self.path == "/v1/status":
                    value = {"enabled": True, "advisor": {"busy": api.busy, "settings_open": False}, "last_consultation_id": "test-job"}
                elif self.path == "/v1/connections":
                    value = {"default_connection_id": "local-test", "connections": [{"id": "local-test", "name": "試験接続"}]}
                elif self.path == "/v1/operations":
                    if self.command == "POST":
                        if api.disconnect_post:
                            self.close_connection = True
                            return
                        value = {"request_id": body["request_id"], "status": "registered"}
                    else:
                        value = {"scope_id": "loaded-game", "enabled": True, "operations": []}
                elif self.path == "/v1/operations/seapower.schedule_sonobuoy_drop/candidates":
                    value = {"scope_id": "loaded-game", "candidates": [{"target_ref": "unit:42"}]}
                elif self.path == "/v1/units/42/reservations":
                    value = {"scope_id": "loaded-game", "unit_id": 42, "count": 2,
                             "reservations": [{"sequence": 1, "ammunition": "custom_buoy", "count": 1}, {"sequence": 2, "kind": "waypoint"}]}
                elif self.path == "/v1/units/42/reservations/clear":
                    if api.disconnect_post:
                        self.close_connection = True
                        return
                    value = {"scope_id": "loaded-game", "unit_id": 42, "removed_count": 2, "count": 0, "reservations": []}
                elif self.path == "/v1/prompts/reset":
                    api.fields["common.json:system"] = "Original\n日本語"
                    value = {"reset": True}
                elif self.path == "/v1/prompts":
                    if self.command == "PATCH": api.fields.update(body["values"])
                    value = {"has_overrides": True, "fields": [{"id": k, "value": v} for k, v in api.fields.items()]}
                elif self.path == "/v1/consultations/cancel":
                    value = {"cancelled": True}
                elif self.path == "/v1/conversation/clear":
                    value = {"cleared": True}
                elif self.path == "/v1/consultations":
                    if api.disconnect_post:
                        self.close_connection = True
                        return
                    if api.fail_post:
                        status, value = api.fail_post
                    else:
                        status, value = 202, {"id": "test-job", "status_url": "/v1/consultations/test-job", "allow_fallback": body["allow_fallback"]}
                elif self.path == "/v1/consultations/test-job":
                    state = api.states.pop(0) if len(api.states) > 1 else api.states[0]
                    value = {"id": "test-job", "state": state, "progress": {"stage": "Gathering", "using_fallback": False},
                             "answer": api.answer if state == "answered" else None,
                             "input": {"data": {"不明": None}}, "retrievals": [{"response": {"ammunition": 42}}]}
                else:
                    status, value = 404, {"error": "not_retained"}
                raw_reply = tool.encode(value).encode("utf-8")
                self.send_response(status)
                self.send_header("Content-Type", "application/json; charset=utf-8")
                self.send_header("Content-Length", str(len(raw_reply)))
                self.end_headers()
                self.wfile.write(raw_reply)
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.base = f"http://127.0.0.1:{self.server.server_address[1]}"
        return self

    def __exit__(self, *args):
        self.server.shutdown(); self.server.server_close(); self.thread.join()

    def run(self, *args):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = tool.main(["--transport", "direct", "--endpoint", self.base, *map(str, args)])
        return code, out.getvalue(), err.getvalue()


class CliTests(unittest.TestCase):
    def test_operations_use_supplied_scope_and_request_id_without_consultation_or_retries(self):
        with MockApi() as api, tempfile.TemporaryDirectory() as temporary:
            self.assertEqual(api.run("operations", "list")[0], 0)
            self.assertEqual(api.run("operations", "candidates", "seapower.schedule_sonobuoy_drop")[0], 0)
            path = Path(temporary) / "order.json"
            body = {"scope_id": "loaded-game", "request_id": "one-reservation", "operation": "seapower.schedule_sonobuoy_drop",
                    "target_ref": "unit:42", "arguments": {"latitude": 26.5, "longitude": 127.5, "ammunition": "custom_buoy", "depth": "above_layer"}}
            path.write_text(tool.encode(body), encoding="utf-8")
            self.assertEqual(api.run("operations", "execute", "--request-file", path)[0], 0)
            self.assertEqual(api.calls[-1][:3], ("POST", "/v1/operations", body))
            api.calls.clear(); api.disconnect_post = True
            self.assertEqual(api.run("operations", "execute", "--request-file", path)[0], 1)
            self.assertEqual(len(api.calls), 1, "A lost acknowledgement retried an order")
            self.assertEqual(api.calls[0][:3], ("POST", "/v1/operations", body))

    def test_reservations_select_explicit_unit_and_scope_and_never_retry_clear(self):
        with MockApi() as api, tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary) / "reservations.json"
            self.assertEqual(api.run("reservations", "list", "--unit-id", "42", "--output", output)[0], 0)
            self.assertEqual(json.loads(output.read_text(encoding="utf-8"))["count"], 2)
            self.assertEqual(api.calls[-1][:3], ("GET", "/v1/units/42/reservations", None))
            self.assertEqual(api.run("reservations", "clear", "--unit-id", "42", "--scope-id", "loaded-game")[0], 0)
            self.assertEqual(api.calls[-1][:3], ("POST", "/v1/units/42/reservations/clear", {"scope_id": "loaded-game"}))
            api.calls.clear()
            self.assertEqual(api.run("reservations", "list", "--unit-id", "-1")[0], 1)
            self.assertEqual(api.calls, [])
            api.disconnect_post = True
            self.assertEqual(api.run("reservations", "clear", "--unit-id", "42", "--scope-id", "loaded-game")[0], 1)
            self.assertEqual(len(api.calls), 1, "A lost acknowledgement retried the reservation reset")
            self.assertEqual(api.calls[0][:3], ("POST", "/v1/units/42/reservations/clear", {"scope_id": "loaded-game"}))

    def test_consult_sends_once_preserves_unicode_and_records_effective_result(self):
        with MockApi() as api, tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); question = root / "question.txt"
            text = "敵への対応？\r\n\"引用\" $() `文字` \\ 𠮷\n"
            question.write_bytes(text.encode("utf-8-sig"))
            output = root / "trial"
            code, raw, error = api.run("consult", "--question-file", question, "--clear-history", "--poll-interval", ".001", "--output", output)
            self.assertEqual((code, error), (0, ""))
            posted = [r for r in api.calls if r[:2] == ("POST", "/v1/consultations")]
            self.assertEqual(len(posted), 1)
            self.assertEqual(posted[0][2], {"question": text, "allow_fallback": False})
            self.assertTrue(all(call[3] == "1" for call in api.calls))
            self.assertEqual([c[1] for c in api.calls[:3]], ["/v1/status", "/v1/conversation/clear", "/v1/consultations"])
            final = json.loads(raw)
            self.assertEqual(tool.load(output / "result.json"), final)
            self.assertEqual((output / "answer.md").read_text(), api.answer)
            self.assertEqual(final["retrievals"][0]["response"]["ammunition"], 42)
            self.assertEqual(len((output / "progress.jsonl").read_text().splitlines()), 3)
            count = len(api.calls)
            self.assertEqual(api.run("consult", "--shortcut", "key_risks", "--output", output)[0], 1)
            self.assertEqual(len(api.calls), count, "An existing capture started another consultation")

    def test_request_file_and_explicit_flags_control_connection_and_fallback(self):
        with MockApi() as api, tempfile.TemporaryDirectory() as temporary:
            request = Path(temporary) / "request.json"
            request.write_text(tool.encode({"shortcut": "next_steps", "connection_id": "file-id", "allow_fallback": True}))
            self.assertEqual(api.run("consult", "--request-file", request, "--connection-id", "override-id", "--no-fallback", "--no-wait")[0], 0)
            self.assertEqual(api.calls[-1][2], {"shortcut": "next_steps", "connection_id": "override-id", "allow_fallback": False})
            self.assertEqual(api.run("consult", "--question", "hello", "--allow-fallback", "--no-wait")[0], 0)
            self.assertTrue(api.calls[-1][2]["allow_fallback"])
            for shortcut in ("own_units", "contacts"):
                self.assertEqual(api.run("consult", "--shortcut", shortcut, "--no-wait")[0], 0)
                self.assertEqual(api.calls[-1][2], {"shortcut": shortcut, "allow_fallback": False})

    def test_delivery_override_uses_flags_or_files_and_rejects_invalid_input_before_clearing(self):
        with MockApi() as api, tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "request.json"
            path.write_text(tool.encode({"shortcut": "key_risks", "situation_delivery": "upfront"}))
            self.assertEqual(api.run("consult", "--request-file", path, "--situation-delivery", "on_demand", "--no-wait")[0], 0)
            self.assertEqual(api.calls[-1][2]["situation_delivery"], "on_demand")
            self.assertEqual(api.run("consult", "--request-file", path, "--no-wait")[0], 0)
            self.assertEqual(api.calls[-1][2]["situation_delivery"], "upfront")
            self.assertEqual(api.run("consult", "--request-file", path, "--situation-delivery", "on_request", "--no-wait")[0], 0)
            self.assertEqual(api.calls[-1][2]["situation_delivery"], "on_request")
            path.write_text(tool.encode({"shortcut": "key_risks", "situation_delivery": "on_request"}))
            self.assertEqual(api.run("consult", "--request-file", path, "--no-wait")[0], 0)
            self.assertEqual(api.calls[-1][2]["situation_delivery"], "on_request")
            self.assertEqual(api.run("consult", "--question", "q", "--no-wait")[0], 0)
            self.assertNotIn("situation_delivery", api.calls[-1][2])
            api.calls.clear()
            for value in (None, False, 1, {}, [], "", "invalid"):
                path.write_text(tool.encode({"question": "q", "situation_delivery": value}))
                self.assertEqual(api.run("consult", "--request-file", path, "--clear-history")[0], 1)
                self.assertEqual(api.calls, [], "Invalid delivery reached the API or cleared history")

    def test_model_override_uses_flags_or_files_without_leaking_to_later_requests(self):
        with MockApi() as api, tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "request.json"
            path.write_text(tool.encode({"question": "q", "connection_id": "nano", "model": "vendor/file-model"}))
            self.assertEqual(api.run("consult", "--request-file", path, "--model", "vendor/flag-model", "--no-wait")[0], 0)
            self.assertEqual(api.calls[-1][2], {"question": "q", "connection_id": "nano", "model": "vendor/flag-model", "allow_fallback": False})
            self.assertEqual(api.run("consult", "--request-file", path, "--no-wait")[0], 0)
            self.assertEqual(api.calls[-1][2]["model"], "vendor/file-model")
            self.assertEqual(api.run("consult", "--question", "q", "--no-wait")[0], 0)
            self.assertNotIn("model", api.calls[-1][2])
            api.calls.clear()
            for model in (None, False, 12, [], "", "   ", "bad\nmodel", "x" * 201):
                path.write_text(tool.encode({"question": "q", "model": model}))
                self.assertEqual(api.run("consult", "--request-file", path, "--clear-history")[0], 1)
                self.assertEqual(api.calls, [], "Invalid models reached the API or cleared history")
            api.fail_post = (403, {"error": "model_override_not_allowed"})
            self.assertEqual(api.run("consult", "--question", "q", "--model", "vendor/test", "--no-wait")[0], 1)
            self.assertEqual(len([c for c in api.calls if c[:2] == ("POST", "/v1/consultations")]), 1)

    def test_plain_adviser_is_controlled_by_flags_or_request_file(self):
        with MockApi() as api, tempfile.TemporaryDirectory() as temporary:
            self.assertEqual(api.run("consult", "--question", "q", "--plain-advisor", "--no-wait")[0], 0)
            self.assertIs(api.calls[-1][2]["plain_advisor"], True)
            path = Path(temporary) / "request.json"
            path.write_text(tool.encode({"question": "q", "plain_advisor": True}))
            self.assertEqual(api.run("consult", "--request-file", path, "--no-plain-advisor", "--no-wait")[0], 0)
            self.assertIs(api.calls[-1][2]["plain_advisor"], False)
            self.assertEqual(api.run("consult", "--request-file", path, "--no-wait")[0], 0)
            self.assertIs(api.calls[-1][2]["plain_advisor"], True)
            api.calls.clear()
            path.write_text('{"question":"q","plain_advisor":"true"}')
            self.assertEqual(api.run("consult", "--request-file", path)[0], 1)
            self.assertEqual(api.calls, [])

    def test_prompt_text_batch_snapshot_restore_and_empty_value(self):
        with MockApi() as api, tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); snapshot = root / "snapshot.json"
            self.assertEqual(api.run("prompts", "get", "--output", snapshot)[0], 0)
            path = root / "prompt.txt"; text = "\r\n日本語と\"引用\" $() `text` 𠮷\r\n終わり\n"
            path.write_bytes(text.encode("utf-8-sig"))
            self.assertEqual(api.run("prompts", "set", "--id", "common.json:system", "--file", path)[0], 0)
            self.assertEqual(api.fields["common.json:system"], text)
            restored = root / "original.txt"
            self.assertEqual(api.run("prompts", "get", "--id", "common.json:system", "--output", restored)[0], 0)
            self.assertEqual(restored.read_bytes().decode("utf-8"), text)
            self.assertEqual(api.run("prompts", "set", "--id", "common.json:system", "--text", "")[0], 0)
            self.assertEqual(api.fields["common.json:system"], "")
            self.assertEqual(api.run("prompts", "set", "--changes-file", snapshot)[0], 0)
            self.assertEqual(api.fields["common.json:system"], "Original\n日本語")
            batch = root / "batch.json"; batch.write_text(tool.encode({"values": {"requests.json:key_risks": "new risk"}}))
            self.assertEqual(api.run("prompts", "set", "--changes-file", batch)[0], 0)
            self.assertEqual(api.fields["requests.json:key_risks"], "new risk")

    def test_timeout_retains_id_and_latest_result_without_cancelling_or_resending(self):
        with MockApi() as api, tempfile.TemporaryDirectory() as temporary:
            api.states = ["waiting"]
            output = Path(temporary) / "trial"
            code, _, error = api.run("consult", "--shortcut", "key_risks", "--wait-timeout", ".001", "--poll-interval", ".001", "--output", output)
            self.assertEqual(code, 1); self.assertIn("not cancelled", error)
            self.assertEqual(tool.load(output / "accepted.json")["id"], "test-job")
            self.assertEqual(tool.load(output / "latest.json")["state"], "waiting")
            api.states = ["answered"]
            result = Path(temporary) / "resumed.json"
            self.assertEqual(api.run("result", "test-job", "--wait", "--output", result)[0], 0)
            self.assertEqual(tool.load(result)["state"], "answered")
            self.assertEqual(len([c for c in api.calls if c[:2] == ("POST", "/v1/consultations")]), 1)
            self.assertFalse(any(c[1].endswith("/cancel") for c in api.calls))

    def test_incomplete_consultation_is_saved_without_resending_or_an_answer(self):
        with MockApi() as api, tempfile.TemporaryDirectory() as temporary:
            api.states = ["waiting", "incomplete"]
            output = Path(temporary) / "trial"
            code, raw, error = api.run("consult", "--question", "q", "--poll-interval", ".001", "--output", output)
            self.assertEqual(code, 1); self.assertEqual(error, "")
            self.assertEqual(json.loads(raw), tool.load(output / "result.json"))
            self.assertEqual(tool.load(output / "result.json")["state"], "incomplete")
            self.assertFalse((output / "answer.md").exists())
            self.assertEqual(len([c for c in api.calls if c[0] == "POST"]), 1)

    def test_provider_failure_stays_in_result_and_returns_nonzero(self):
        with MockApi() as api, tempfile.TemporaryDirectory() as temporary:
            api.states = ["failed"]
            output = Path(temporary) / "result.json"
            code, raw, error = api.run("result", "test-job", "--wait", "--output", output)
            self.assertEqual(code, 1); self.assertEqual(error, "")
            self.assertEqual(json.loads(raw), tool.load(output))
            self.assertEqual(tool.load(output)["state"], "failed")

    def test_http_error_body_is_preserved_without_retry(self):
        with MockApi() as api, tempfile.TemporaryDirectory() as temporary:
            api.fail_post = (403, {"error": "connection_not_allowed", "detail": "生のエラー"})
            output = Path(temporary) / "failed"
            code, _, error = api.run("consult", "--shortcut", "key_risks", "--output", output)
            self.assertEqual(code, 1); self.assertIn("HTTP 403", error); self.assertIn("生のエラー", error)
            self.assertEqual((output / "error.txt").read_text().strip(), error.strip())
            self.assertEqual(len([c for c in api.calls if c[:2] == ("POST", "/v1/consultations")]), 1)

    def test_lost_post_acknowledgement_is_not_retried(self):
        with MockApi() as api:
            api.disconnect_post = True
            self.assertEqual(api.run("consult", "--shortcut", "key_risks")[0], 1)
            self.assertEqual(len([c for c in api.calls if c[:2] == ("POST", "/v1/consultations")]), 1)

    def test_unavailable_adviser_and_invalid_inputs_have_no_side_effects(self):
        with MockApi() as api, tempfile.TemporaryDirectory() as temporary:
            api.busy = True
            self.assertEqual(api.run("consult", "--shortcut", "key_risks", "--clear-history")[0], 1)
            self.assertTrue(all(c[0] == "GET" for c in api.calls))
            api.calls.clear()
            bad = Path(temporary) / "invalid.json"; bad.write_text('{"question":"q","allow_fallback":"false"}')
            for args in (("consult", "--request-file", bad), ("prompts", "set", "--text", "missing id"),
                         ("consult", "--shortcut", "key_risks", "--no-wait", "--answer-only")):
                self.assertEqual(api.run(*args)[0], 1)
            self.assertEqual(api.calls, [])

    def test_discovery_is_offline_and_other_commands_respect_manifest_endpoint(self):
        with MockApi() as api, tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "developer-api.json"
            manifest = {"endpoint": api.base, "process_id": 123, "default_connection_id": "test", "connections": []}
            path.write_text(tool.encode(manifest))
            out = io.StringIO()
            with contextlib.redirect_stdout(out), patch.object(tool, "Client", side_effect=AssertionError("Unexpected HTTP")):
                self.assertEqual(tool.main(["--discovery-file", str(path), "discover"]), 0)
            self.assertEqual(json.loads(out.getvalue()), manifest)
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(tool.main(["--transport", "direct", "--discovery-file", str(path), "connections"]), 0)
            self.assertEqual(api.calls[-1][1], "/v1/connections")

    def test_explicit_clear_cancel_and_reset_use_the_expected_empty_bodies(self):
        with MockApi() as api:
            for arguments, path in ((("conversation", "clear"), "/v1/conversation/clear"),
                                    (("cancel",), "/v1/consultations/cancel"),
                                    (("prompts", "reset"), "/v1/prompts/reset")):
                self.assertEqual(api.run(*arguments)[0], 0)
                self.assertEqual(api.calls[-1][:3], ("POST", path, {}))


@unittest.skipUnless(os.environ.get("ADVISOR_TEST_POWERSHELL"), "Set ADVISOR_TEST_POWERSHELL for the Windows loopback bridge check.")
class WindowsBridgeTests(unittest.TestCase):
    def test_real_powershell_bridge_preserves_stdin_unicode_and_http_error(self):
        executable = os.environ["ADVISOR_TEST_POWERSHELL"]
        server = r'''[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$ErrorActionPreference = 'Stop'
$probe = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
$probe.Start(); $port=$probe.LocalEndpoint.Port; $probe.Stop()
$listener=New-Object Net.HttpListener
$listener.Prefixes.Add(('http://127.0.0.1:'+$port+'/')); $listener.Start()
[Console]::WriteLine($port); [Console]::Out.Flush()
try {
 for ($i=0; $i -lt 2; $i++) {
  $pending=$listener.GetContextAsync()
  if (!$pending.Wait(15000)) { throw 'No mock request arrived.' }
  $context=$pending.Result
  $reader=New-Object IO.StreamReader($context.Request.InputStream,[Text.Encoding]::UTF8)
  $body=$reader.ReadToEnd(); $reader.Dispose()
  $value=@{method=$context.Request.HttpMethod; header=$context.Request.Headers['X-Advisor-Developer']; body=$body; path=$context.Request.RawUrl}
  if ($i -eq 1) { $context.Response.StatusCode=429; $value=@{error='mock_limit'; detail='試験'} }
  $bytes=[Text.Encoding]::UTF8.GetBytes(($value|ConvertTo-Json -Compress -Depth 8))
  $context.Response.ContentType='application/json; charset=utf-8'; $context.Response.ContentLength64=$bytes.Length
  $context.Response.OutputStream.Write($bytes,0,$bytes.Length); $context.Response.Close()
 }
} finally { $listener.Stop(); $listener.Close() }
'''
        command = base64.b64encode(server.encode("utf-16-le")).decode()
        process = subprocess.Popen([executable, "-NoProfile", "-NonInteractive", "-EncodedCommand", command], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        try:
            port = int(process.stdout.readline().decode("utf-8-sig").strip())
            client = tool.Client(f"http://127.0.0.1:{port}", "powershell", 5, executable)
            body = {"values": {"common.json:system": "\r\n日本語の\"引用\"、$()、`commands`、𠮷\n" * 500}}
            response = client.request("PATCH", "/v1/prompts", body)
            self.assertEqual(json.loads(response["body"]), body)
            self.assertEqual(response["method"], "PATCH"); self.assertEqual(response["header"], "1")
            with self.assertRaises(tool.ApiError) as caught:
                client.request("GET", "/v1/status")
            self.assertEqual(caught.exception.status, 429)
            self.assertEqual(json.loads(caught.exception.body), {"error": "mock_limit", "detail": "試験"})
            _, error = process.communicate(timeout=10)
            self.assertEqual(process.returncode, 0, error.decode(errors="replace"))
        finally:
            if process.poll() is None:
                process.kill(); process.communicate()


if __name__ == "__main__":
    unittest.main()
