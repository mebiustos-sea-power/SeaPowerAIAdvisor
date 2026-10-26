#!/usr/bin/env python3
"""Operate AI Advisor's developer local API (Python 3.10+, standard library only)."""
import argparse
import base64
import json
import math
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request


class ToolError(Exception):
    pass


class ApiError(ToolError):
    def __init__(self, status, body):
        super().__init__(f"HTTP {status}\n{body}")
        self.status, self.body = status, body


def encode(value):
    return json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False) + "\n"


def read_text(path):
    return Path(path).read_bytes().decode("utf-8-sig")


def write_text(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(value.encode("utf-8"))


def load(path):
    return json.loads(read_text(path))


def save(path, value):
    if path is not None:
        write_text(path, encode(value))


def is_wsl():
    return sys.platform == "linux" and "microsoft" in Path("/proc/sys/kernel/osrelease").read_text().lower()


def powershell_path(explicit=None):
    found = explicit or shutil.which("powershell.exe")
    if not found and Path("/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe").is_file():
        found = "/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe"
    if not found:
        raise ToolError("Windows PowerShell was not found; specify --powershell or --transport direct.")
    return found


def run_powershell(executable, script, payload=None, timeout=25):
    command = base64.b64encode(script.encode("utf-16-le")).decode("ascii")
    result = subprocess.run([executable, "-NoProfile", "-NonInteractive", "-EncodedCommand", command],
                            input=None if payload is None else json.dumps(payload, ensure_ascii=False).encode("utf-8"),
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=timeout)
    if result.returncode:
        raise ToolError(result.stderr.decode("utf-8-sig", errors="replace").strip() or "PowerShell request failed.")
    return result.stdout.decode("utf-8-sig")


def discovery_path(explicit=None, powershell=None):
    if explicit:
        return Path(explicit)
    if is_wsl():
        script = "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false); [Console]::Write([IO.Path]::GetTempPath())"
        windows = run_powershell(powershell_path(powershell), script).strip()
        if len(windows) < 3 or windows[1:3] != ":\\":
            raise ToolError("Cannot map Windows Temp; specify --discovery-file.")
        root = Path("/mnt") / windows[0].lower() / windows[3:].replace("\\", "/")
    else:
        root = Path(tempfile.gettempdir())
    return root / "SeaPowerAIAdvisor" / "developer-api.json"


def endpoint(value):
    parsed = urllib.parse.urlsplit(value)
    if (parsed.scheme != "http" or parsed.hostname != "127.0.0.1" or parsed.username or parsed.password
            or parsed.path not in ("", "/") or parsed.query or parsed.fragment):
        raise ToolError("The endpoint must be an HTTP IPv4 loopback URL, e.g. http://127.0.0.1:18765.")
    _ = parsed.port  # Surface malformed ports before sending anything.
    return value.rstrip("/")


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


# The command is fixed. URLs, Unicode text and JSON bodies travel via stdin, not shell source.
POWERSHELL_HTTP = r'''[Console]::InputEncoding = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$ErrorActionPreference = 'Stop'
try {
    $inputData = [Console]::In.ReadToEnd() | ConvertFrom-Json
    $request = [System.Net.HttpWebRequest]::Create([string]$inputData.url)
    $request.Method = [string]$inputData.method
    $request.Proxy = $null
    $request.AllowAutoRedirect = $false
    $request.Timeout = [int]$inputData.timeout_ms
    $request.ReadWriteTimeout = [int]$inputData.timeout_ms
    $request.Headers['X-Advisor-Developer'] = '1'
    if ($null -ne $inputData.body) {
        $bytes = [Text.Encoding]::UTF8.GetBytes([string]$inputData.body)
        $request.ContentType = 'application/json; charset=utf-8'
        $request.ContentLength = $bytes.Length
        $stream = $request.GetRequestStream()
        try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
    }
    try { $response = $request.GetResponse() }
    catch [System.Net.WebException] {
        if ($null -eq $_.Exception.Response) { throw }
        $response = $_.Exception.Response
    }
    try {
        $reader = New-Object IO.StreamReader($response.GetResponseStream(), [Text.Encoding]::UTF8)
        try { $body = $reader.ReadToEnd() } finally { $reader.Dispose() }
        [Console]::Write((@{status=[int]$response.StatusCode; body=$body} | ConvertTo-Json -Compress))
    } finally { $response.Dispose() }
} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
'''


class Client:
    def __init__(self, base, transport="auto", timeout=20, powershell=None):
        self.base, self.timeout = endpoint(base), timeout
        self.transport = ("powershell" if is_wsl() else "direct") if transport == "auto" else transport
        self.powershell = powershell_path(powershell) if self.transport == "powershell" else None
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())

    def request(self, method, path, body=None):
        if not path.startswith("/v1/") or "?" in path or "#" in path:
            raise ToolError("Invalid local API path.")
        wire = None if body is None else json.dumps(body, ensure_ascii=False, allow_nan=False)
        if self.transport == "powershell":
            raw = run_powershell(self.powershell, POWERSHELL_HTTP,
                                 {"url": self.base + path, "method": method, "body": wire,
                                  "timeout_ms": math.ceil(self.timeout * 1000)}, self.timeout + 5)
            reply = json.loads(raw)
            status, text = reply["status"], reply["body"]
        else:
            request = urllib.request.Request(self.base + path, method=method,
                data=None if wire is None else wire.encode("utf-8"),
                headers={"X-Advisor-Developer": "1", "Content-Type": "application/json; charset=utf-8"})
            try:
                response = self.opener.open(request, timeout=self.timeout)
            except urllib.error.HTTPError as error:
                response = error
            with response:
                status, text = response.code, response.read().decode("utf-8-sig")
        if not 200 <= status < 300:
            raise ApiError(status, text)
        value = json.loads(text)
        if not isinstance(value, dict):
            raise ToolError("The local API did not return a JSON object.")
        return value


def positive(value):
    number = float(value)
    if not math.isfinite(number) or number <= 0:
        raise argparse.ArgumentTypeError("must be a finite positive number")
    return number


def consultation_request(args):
    if args.request_file:
        body = load(args.request_file)
        if not isinstance(body, dict):
            raise ToolError("The consultation request file must contain an object.")
    elif args.question_file:
        body = {"question": read_text(args.question_file)}
    elif args.question is not None:
        body = {"question": args.question}
    else:
        body = {"shortcut": args.shortcut}
    if set(body) - {"question", "shortcut", "connection_id", "allow_fallback", "plain_advisor", "model", "situation_delivery", "allow_operations"}:
        raise ToolError("Unsupported consultation request field.")
    if ("question" in body) == ("shortcut" in body):
        raise ToolError("Specify exactly one question or shortcut.")
    for key in ("question", "shortcut", "connection_id"):
        if key in body and (not isinstance(body[key], str) or not body[key].strip()):
            raise ToolError(f"{key} must be a nonempty string.")
    if args.connection_id is not None:
        body["connection_id"] = args.connection_id
    if args.model is not None:
        body["model"] = args.model
    if "model" in body and (not isinstance(body["model"], str) or not body["model"].strip()
                            or len(body["model"]) > 200 or any(ord(c) < 32 or 127 <= ord(c) <= 159 for c in body["model"])):
        raise ToolError("model must be a nonempty model ID of at most 200 characters without control characters.")
    if args.situation_delivery is not None:
        body["situation_delivery"] = args.situation_delivery
    if "situation_delivery" in body and body["situation_delivery"] not in ("on_demand", "upfront", "on_request"):
        raise ToolError("situation_delivery must be on_demand, upfront or on_request.")
    if args.allow_fallback is not None:
        body["allow_fallback"] = args.allow_fallback
    if args.plain_advisor is not None:
        body["plain_advisor"] = args.plain_advisor
    if "plain_advisor" in body and type(body["plain_advisor"]) is not bool:
        raise ToolError("plain_advisor must be a boolean.")
    if args.allow_operations is not None:
        body["allow_operations"] = args.allow_operations
    if "allow_operations" in body and type(body["allow_operations"]) is not bool:
        raise ToolError("allow_operations must be a boolean.")
    body.setdefault("allow_fallback", False)
    if type(body["allow_fallback"]) is not bool:
        raise ToolError("allow_fallback must be a boolean.")
    return body


def prompt_changes(args):
    if args.changes_file:
        if args.id:
            raise ToolError("Use --id with --text/--file, or --changes-file for a batch.")
        changes = load(args.changes_file)
        if isinstance(changes, dict) and "fields" in changes:
            # A prompts-get snapshot can be reapplied without losing multiline values.
            changes = {"values": {row["id"]: row["value"] for row in changes["fields"]}}
        if not isinstance(changes, dict) or set(changes) != {"values"}:
            raise ToolError('Batch prompt JSON must contain {"values": {"file.json:key": "text"}} or a prompts-get snapshot.')
    else:
        if not args.id:
            raise ToolError("--id is required with --text or --file.")
        text = args.text if args.text is not None else read_text(args.file)
        changes = {"values": {args.id: text}}
    values = changes["values"]
    if not isinstance(values, dict) or not all(isinstance(k, str) and isinstance(v, str) for k, v in values.items()):
        raise ToolError("Prompt values must be strings; empty strings are allowed.")
    return changes


def consultation_path(identifier):
    return "/v1/consultations/" + urllib.parse.quote(identifier, safe="")


def wait_result(client, identifier, args, record=lambda value: None, progress_file=None):
    started, previous = time.monotonic(), None
    while True:
        value = client.request("GET", consultation_path(identifier))
        record(value)
        progress = {"state": value.get("state"), "progress": value.get("progress")}
        if progress != previous:
            if progress_file:
                with Path(progress_file).open("a", encoding="utf-8", newline="") as stream:
                    stream.write(json.dumps(progress, ensure_ascii=False) + "\n")
            if args.progress:
                print(json.dumps(progress, ensure_ascii=False), file=sys.stderr, flush=True)
            previous = progress
        state = value.get("state")
        if state not in ("starting", "waiting", "answered", "incomplete", "failed", "cancelled"):
            raise ToolError("Unknown consultation state: " + str(state))
        if state not in ("starting", "waiting"):
            return value
        if args.wait_timeout is not None and time.monotonic() - started >= args.wait_timeout:
            raise ToolError(f"Stopped waiting for {identifier}; it was not cancelled. Resume with result {identifier} --wait.")
        time.sleep(args.poll_interval)


def print_result(value, args):
    if not args.quiet:
        if getattr(args, "answer_only", False):
            print(value.get("answer") or "")
        elif isinstance(value, str):
            print(value, end="")
        else:
            print(encode(value), end="")


def result_exit(value):
    return 1 if value.get("state") in ("incomplete", "failed", "cancelled") else 0


def consult(client, args):
    if args.no_wait and args.answer_only:
        raise ToolError("--answer-only requires waiting for a final answer.")
    body = consultation_request(args)
    directory = Path(args.output) if args.output else None
    if directory:
        directory.mkdir(parents=True, exist_ok=False)
        save(directory / "request.json", body)
    try:
        status = client.request("GET", "/v1/status")
        if directory:
            save(directory / "status-before.json", status)
        adviser = status.get("advisor") or {}
        if not status.get("enabled") or not adviser or adviser.get("busy") or adviser.get("settings_open"):
            raise ToolError("The adviser is unavailable, busy, or settings are open; no consultation was sent.")
        if args.clear_history:
            cleared = client.request("POST", "/v1/conversation/clear", {})
            if directory:
                save(directory / "clear-response.json", cleared)
        # Never retry this POST: a lost acknowledgement does not prove it was rejected.
        accepted = client.request("POST", "/v1/consultations", body)
        if directory:
            save(directory / "accepted.json", accepted)
        identifier = accepted["id"]
        if args.no_wait:
            print_result(accepted, args)
            return 0
        def record(value):
            if directory:
                save(directory / "latest.json", value)
        result = wait_result(client, identifier, args, record,
                             directory / "progress.jsonl" if directory else None)
        if directory:
            save(directory / "result.json", result)
            if isinstance(result.get("answer"), str):
                write_text(directory / "answer.md", result["answer"])
        print_result(result, args)
        return result_exit(result)
    except (ToolError, OSError, ValueError, subprocess.SubprocessError) as error:
        if directory:
            write_text(directory / "error.txt", str(error) + "\n")
        raise


def add_output(parser):
    parser.add_argument("--output", help="Save response as UTF-8 JSON (raw text with prompts get --id).")


def add_wait(parser):
    parser.add_argument("--poll-interval", type=positive, default=0.8, help="Polling seconds (default: 0.8).")
    parser.add_argument("--wait-timeout", type=positive, help="Stop waiting after this many seconds; does not cancel the adviser.")
    parser.add_argument("--progress", action="store_true", help="Print changed progress to stderr.")
    parser.add_argument("--answer-only", action="store_true", help="Print only the final Markdown answer.")


def parser():
    root = argparse.ArgumentParser(description=__doc__)
    root.add_argument("--endpoint", help="Override discovery with an explicit loopback URL.")
    root.add_argument("--discovery-file", help="Override the Temp discovery JSON path.")
    root.add_argument("--transport", choices=("auto", "direct", "powershell"), default="auto", help="auto uses Windows PowerShell in WSL, direct HTTP elsewhere.")
    root.add_argument("--powershell", help="Windows PowerShell executable for the WSL bridge.")
    root.add_argument("--timeout", type=positive, default=20, help="Timeout per local API request in seconds (default: 20).")
    root.add_argument("--quiet", action="store_true", help="Suppress stdout; errors still go to stderr.")
    commands = root.add_subparsers(dest="command", required=True)
    for name in ("discover", "status", "connections", "cancel"):
        add_output(commands.add_parser(name))
    notices = commands.add_parser("notices").add_subparsers(dest="notice_command", required=True)
    add_output(notices.add_parser("preview"))
    operations = commands.add_parser("operations").add_subparsers(dest="operation_command", required=True)
    add_output(operations.add_parser("list"))
    candidates = operations.add_parser("candidates"); candidates.add_argument("operation"); add_output(candidates)
    plan = operations.add_parser("submit", help="Register one explicitly selected composed plan without AI or approval UI."); plan.add_argument("--request-file", required=True); add_output(plan)
    reservations = commands.add_parser("reservations").add_subparsers(dest="reservation_command", required=True)
    for name in ("list", "clear"):
        command = reservations.add_parser(name)
        command.add_argument("--unit-id", type=int, required=True, help="The game's numeric unit_id.")
        if name == "clear": command.add_argument("--scope-id", required=True, help="Use scope_id from a current reservation list; manual reservations are also removed.")
        add_output(command)
    prompts = commands.add_parser("prompts").add_subparsers(dest="prompt_command", required=True)
    get = prompts.add_parser("get"); get.add_argument("--id", help="Return one field's original text."); add_output(get)
    put = prompts.add_parser("set"); put.add_argument("--id")
    source = put.add_mutually_exclusive_group(required=True)
    source.add_argument("--text", help="Prompt text, including an empty string.")
    source.add_argument("--file", help="UTF-8 prompt text file.")
    source.add_argument("--changes-file", help="JSON values batch or a saved prompts-get snapshot.")
    add_output(put)
    add_output(prompts.add_parser("reset"))
    conversation = commands.add_parser("conversation").add_subparsers(dest="conversation_command", required=True)
    add_output(conversation.add_parser("clear"))
    request = commands.add_parser("consult")
    question = request.add_mutually_exclusive_group(required=True)
    question.add_argument("--question"); question.add_argument("--question-file")
    question.add_argument("--shortcut", choices=("key_risks", "next_steps", "radar_operations", "own_units", "contacts", "more_detail"))
    question.add_argument("--request-file", help="Full consultation request JSON.")
    request.add_argument("--connection-id", help="Omit to use the developer default.")
    request.add_argument("--model", help="Override this consultation model when permitted in Local API settings; saved connections are unchanged.")
    request.add_argument("--situation-delivery", choices=("on_demand", "upfront", "on_request"),
                         help="Override situation delivery for this consultation; omit to use the saved connection setting.")
    fallback = request.add_mutually_exclusive_group()
    fallback.add_argument("--allow-fallback", dest="allow_fallback", action="store_true")
    fallback.add_argument("--no-fallback", dest="allow_fallback", action="store_false")
    request.set_defaults(allow_fallback=None)
    request.add_argument("--plain-advisor", action=argparse.BooleanOptionalAction, default=None,
                         help="Use Michael Reed with empty personality and shared user instructions for this consultation.")
    operation_policy = request.add_mutually_exclusive_group()
    operation_policy.add_argument("--allow-operations", dest="allow_operations", action="store_true", help="Permit operation tools; WIP and approval still apply.")
    operation_policy.add_argument("--no-operations", dest="allow_operations", action="store_false", help="Disable operation tools for this consultation.")
    request.set_defaults(allow_operations=None)
    request.add_argument("--clear-history", action="store_true", help="Explicitly clear conversation before this consultation.")
    request.add_argument("--no-wait", action="store_true", help="Return the accepted ID immediately.")
    request.add_argument("--output", help="New result directory; an existing directory is rejected before API access.")
    add_wait(request)
    result = commands.add_parser("result"); result.add_argument("id")
    result.add_argument("--wait", action="store_true"); add_wait(result); add_output(result)
    result.add_argument("--answer-file"); result.add_argument("--progress-file")
    return root


def main(argv=None):
    args = parser().parse_args(argv)
    try:
        if args.command == "discover":
            value = load(discovery_path(args.discovery_file, args.powershell))
            save(args.output, value); print_result(value, args)
            return 0
        base = args.endpoint
        if base is None:
            base = load(discovery_path(args.discovery_file, args.powershell))["endpoint"]
        client = Client(base, args.transport, args.timeout, args.powershell)
        if args.command == "consult":
            return consult(client, args)
        if args.command == "result":
            if args.progress_file:
                Path(args.progress_file).parent.mkdir(parents=True, exist_ok=True)
            if args.wait:
                value = wait_result(client, args.id, args, lambda result: save(args.output, result), args.progress_file)
            else:
                value = client.request("GET", consultation_path(args.id))
            if args.answer_file and isinstance(value.get("answer"), str):
                write_text(args.answer_file, value["answer"])
        elif args.command == "operations":
            if args.operation_command == "list":
                value = client.request("GET", "/v1/operations")
            elif args.operation_command == "candidates":
                value = client.request("GET", "/v1/operations/" + urllib.parse.quote(args.operation, safe="") + "/candidates")
            else:
                body = load(args.request_file)
                if not isinstance(body, dict):
                    raise ToolError("The operation request must be a JSON object.")
                # Preserve scope and request IDs. Never retry or silently rebind an order to another loaded game.
                value = client.request("POST", "/v1/operation-plans", body)
        elif args.command == "reservations":
            if args.unit_id <= 0: raise ToolError("unit-id must be a positive integer.")
            path = "/v1/units/" + str(args.unit_id) + "/reservations"
            if args.reservation_command == "list":
                value = client.request("GET", path)
            else:
                # Never infer a new battle scope or retry a destructive reset after a lost response.
                value = client.request("POST", path + "/clear", {"scope_id": args.scope_id})
        elif args.command == "prompts":
            if args.prompt_command == "get":
                value = client.request("GET", "/v1/prompts")
                if args.id:
                    matches = [field["value"] for field in value["fields"] if field["id"] == args.id]
                    if not matches:
                        raise ToolError("Unknown prompt ID: " + args.id)
                    value = matches[0]
            elif args.prompt_command == "set":
                value = client.request("PATCH", "/v1/prompts", prompt_changes(args))
            else:
                value = client.request("POST", "/v1/prompts/reset", {})
        elif args.command == "notices":
            value = client.request("POST", "/v1/notices/preview", {})
        elif args.command == "conversation":
            value = client.request("POST", "/v1/conversation/clear", {})
        elif args.command == "cancel":
            value = client.request("POST", "/v1/consultations/cancel", {})
        else:
            value = client.request("GET", "/v1/" + args.command)
        if isinstance(value, str):
            if args.output:
                write_text(args.output, value)
        else:
            save(args.output, value)
        print_result(value, args)
        return result_exit(value) if isinstance(value, dict) else 0
    except (ToolError, OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
        print(str(error), file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("Stopped locally; active consultation was not cancelled. Use status/result or cancel.", file=sys.stderr)
        return 130


if __name__ == "__main__":
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")
    raise SystemExit(main())
