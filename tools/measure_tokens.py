#!/usr/bin/env python3
"""Capture and compare AI Advisor consultations independently of the game (Python 3.10+).

  python tools/measure_tokens.py capture LOG --output artifacts/token-measurements/before
  python tools/measure_tokens.py measure artifacts/token-measurements/before --settings LOCAL_GEMINI_JSON
  python tools/measure_tokens.py compare BEFORE AFTER

capture is offline. Only measure calls Google's countTokens endpoint; it never generates advice.
Credentials come from an explicitly selected local settings file or GEMINI_API_KEY /
GEMINI_SUB_API_KEY, are held in memory, and are not copied into the capture.

The measured input is a GenerateContent representation of the logged visible context,
not a reproduction of Interactions server internals or an invoice. Text is preserved;
function results use a {"result": text} response object. Opaque signatures and thought
steps cannot be reconstructed as input and are explicitly recorded as omitted.
References: https://ai.google.dev/api/tokens and https://ai.google.dev/api/generate-content
"""
import argparse
import copy
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import socket
import sys
import urllib.error
import urllib.request

USAGE = ("total_input_tokens", "total_output_tokens", "total_thought_tokens",
         "total_tokens", "total_cached_tokens", "total_tool_use_tokens")
METHOD = "visible-context-v1"
LIMITATIONS = [
    "GenerateContent input reconstructed from logged Interactions context; not billing usage.",
    "Function results retain original text inside a response.result object; API framing may differ.",
    "Thought steps and opaque signatures are omitted; hidden server context cannot be recovered.",
    "Counts cover attempted requests, including failures; an attempted input is not proof of a charge.",
]


class MeasurementError(Exception):
    pass


def require(condition, message):
    if not condition:
        raise MeasurementError(message)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def encode(value):
    return (json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False) + "\n").encode("utf-8")


def load(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def integer(value):
    return value if type(value) is int and value >= 0 else None


def model_name(value):
    require(isinstance(value, str), "Missing request model.")
    name = value.removeprefix("models/")
    require(re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]*", name), "Unsupported model resource name.")
    return name


def body(record):
    value = record.get("body_json")
    if isinstance(value, str):
        value = json.loads(value)
    require(isinstance(value, dict), "Missing JSON body in a request/response record.")
    return value


def select_consultation(raw, consultation_id=None):
    lines = [(line, json.loads(line.decode("utf-8-sig"))) for line in raw.splitlines(keepends=True) if line.strip()]
    starts = [r for _, r in lines if r.get("event") == "consultation_started"
              and not str(r.get("consultation_id", "")).startswith("connection-test-")]
    require(starts, "No advisor consultation found (connection tests are excluded).")
    selected = consultation_id or starts[-1]["consultation_id"]
    entries = [(line, r) for line, r in lines if r.get("consultation_id") == selected]
    require(entries and entries[0][1].get("event") == "consultation_started", "Consultation start is missing.")
    rows = [r for _, r in entries]
    require([r.get("sequence") for r in rows] == list(range(1, len(rows) + 1)), "Consultation log has missing, repeated or out-of-order records.")
    require(rows[-1].get("event") == "consultation_finished", "The latest consultation is unfinished or truncated; wait for completion.")
    return b"".join(line for line, _ in entries), rows


def texts(content):
    require(isinstance(content, list) and content, "Expected nonempty text content.")
    require(all(isinstance(p, dict) and p.get("type") == "text" and isinstance(p.get("text"), str)
                for p in content), "Unsupported non-text content; refusing a partial count.")
    return [p["text"] for p in content]


def append_parts(contents, role, parts):
    if contents and contents[-1]["role"] == role:
        contents[-1]["parts"].extend(parts)
    else:
        contents.append({"role": role, "parts": parts})


def add_steps(contents, steps, omitted):
    require(isinstance(steps, list), "Missing interaction steps.")
    for step in steps:
        kind = step.get("type")
        omitted["opaque_signatures"] += int(bool(step.get("signature")))
        if kind == "thought":
            omitted["thought_steps"] += 1
        elif kind in ("user_input", "model_output"):
            append_parts(contents, "user" if kind == "user_input" else "model",
                         [{"text": t} for t in texts(step.get("content"))])
        elif kind == "function_call":
            require(isinstance(step.get("arguments"), dict) and isinstance(step.get("name"), str)
                    and isinstance(step.get("id"), str), "Malformed function call.")
            append_parts(contents, "model", [{"functionCall": {"id": step["id"], "name": step["name"], "args": copy.deepcopy(step["arguments"])}}])
        elif kind == "function_result":
            values = texts(step.get("result"))
            require(isinstance(step.get("call_id"), str) and isinstance(step.get("name"), str), "Malformed function result.")
            append_parts(contents, "user", [{"functionResponse": {"id": step["call_id"], "name": step["name"],
                         "response": {"result": values[0] if len(values) == 1 else values}}}])
        else:
            raise MeasurementError("Unsupported interaction step type; refusing a partial count: " + str(kind))


def prepare(records):
    responses = {r["api_request"]: r for r in records if r.get("event") == "response"}
    contexts, prepared = {}, []
    for request in (r for r in records if r.get("event") == "request"):
        number, wire = request["api_request"], body(request)
        model, slot = model_name(wire.get("model")), request.get("key_slot", "primary")
        require(slot in ("primary", "fallback"), "Unknown API key slot.")
        previous = wire.get("previous_interaction_id")
        if previous is None:
            contents, omitted = [], {"thought_steps": 0, "opaque_signatures": 0}
        else:
            require(previous in contexts, "Cannot resolve previous_interaction_id from this consultation log.")
            prior = contexts[previous]
            require((model, slot) == (prior["model"], prior["slot"]), "An interaction chain changed model or key slot without resetting context.")
            contents, omitted = copy.deepcopy(prior["contents"]), copy.deepcopy(prior["omitted"])
        add_steps(contents, wire.get("input"), omitted)
        generated = {"model": "models/" + model, "contents": copy.deepcopy(contents)}
        if "system_instruction" in wire:
            require(isinstance(wire["system_instruction"], str), "Unsupported system instruction.")
            generated["systemInstruction"] = {"parts": [{"text": wire["system_instruction"]}]}
        if wire.get("tools"):
            declarations = []
            for tool in wire["tools"]:
                require(tool.get("type") == "function" and isinstance(tool.get("parameters"), dict), "Unsupported tool definition.")
                declarations.append({"name": tool["name"], "description": tool.get("description", ""), "parametersJsonSchema": copy.deepcopy(tool["parameters"])})
            generated["tools"] = [{"functionDeclarations": declarations}]
        response_record = responses.get(number)
        response = None
        if response_record and response_record.get("body_complete"):
            try:
                response = body(response_record)
            except json.JSONDecodeError:
                pass  # Preserve the HTTP status; this response cannot supply usage/context.
        usage = response.get("usage", {}) if response else {}
        require(isinstance(usage, dict), "Malformed usage object.")
        calls = [s for s in response.get("steps", []) if s.get("type") == "function_call"] if response else []
        prepared.append({"api_request": number, "model": model, "key_slot": slot,
                         "previous_interaction_id": previous,
                         "http_status": response_record.get("http_status") if response_record else None,
                         "body_complete": response_record.get("body_complete") if response_record else False,
                         "api_reported": {k: integer(usage.get(k)) for k in USAGE},
                         "invalid_usage_fields": [k for k in USAGE if k in usage and integer(usage[k]) is None],
                         "retrieval_requests": [{"name": c.get("name"), "arguments": c.get("arguments")} for c in calls],
                         "omitted_context": copy.deepcopy(omitted), "count_status": "pending", "count_tokens": None,
                         "count_request": {"generateContentRequest": generated}})
        if response and response.get("id") and isinstance(response.get("steps"), list):
            require(response["id"] not in contexts, "Duplicate interaction response ID.")
            add_steps(contents, response["steps"], omitted)
            contexts[response["id"]] = {"model": model, "slot": slot, "contents": contents, "omitted": omitted}
    require(prepared, "No API requests in the consultation.")
    require([r["api_request"] for r in prepared] == list(range(1, len(prepared) + 1)), "Missing or duplicate API requests.")
    return prepared


def total(values):
    values = list(values)
    return sum(values) if values and all(v is not None for v in values) else None


def summarize(report):
    rows = report["requests"]
    report["totals"] = {"count_tokens": total(r["count_tokens"] for r in rows),
                        "count_tokens_known_subtotal": sum(r["count_tokens"] or 0 for r in rows),
                        "measured_requests": sum(r["count_status"] == "measured" for r in rows),
                        "api_reported": {k: total(r["api_reported"][k] for r in rows) for k in USAGE}}
    return report


def table(report):
    lines = ["# " + report["label"].replace("\n", " "), "", "Reconstructed input comparison; not billing usage.", "",
             "| API | Model | countTokens input | Reported input | Output | Thought | HTTP |",
             "|---|---|---:|---:|---:|---:|---:|"]
    display = lambda x: "—" if x is None else str(x)
    for r in report["requests"]:
        u = r["api_reported"]
        lines.append("| " + " | ".join(map(display, [r["api_request"], r["model"], r["count_tokens"],
                     u["total_input_tokens"], u["total_output_tokens"], u["total_thought_tokens"], r["http_status"]])) + " |")
    lines += ["", "countTokens input total: " + display(report["totals"]["count_tokens"]), "",
              *["- " + note for note in report["limitations"]]]
    return "\n".join(lines) + "\n"


def save_report(directory, report):
    summarize(report)
    (directory / "measurement.json").write_bytes(encode(report))
    (directory / "summary.md").write_text(table(report), encoding="utf-8")


def capture(log, output, consultation_id=None, label=None):
    raw = log.read_bytes()
    selected, records = select_consultation(raw, consultation_id)
    rows = prepare(records)
    require(not output.exists(), "Capture directory already exists; choose a new directory.")
    output.mkdir(parents=True)
    (output / "ai-communication.jsonl").write_bytes(selected)
    (output / "count-requests").mkdir()
    for row in rows:
        data = encode(row.pop("count_request"))
        name = "count-requests/%03d.json" % row["api_request"]
        (output / name).write_bytes(data)
        row.update({"count_request_file": name, "count_request_sha256": digest(data), "count_request_bytes": len(data)})
    report = {"schema_version": 1, "method": METHOD, "label": label or output.name,
              "captured_at_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
              "source": {"path": str(log.resolve()), "sha256": digest(raw), "capture_sha256": digest(selected)},
              "consultation_id": records[0]["consultation_id"], "question": records[0].get("display_question"),
              "outcome": records[-1].get("outcome"), "query_rounds": records[-1].get("query_rounds"),
              "api_requests": len(rows), "limitations": LIMITATIONS, "requests": rows}
    save_report(output, report)
    return report


def credentials(settings=None):
    keys = {"primary": os.environ.get("GEMINI_API_KEY", "").strip(), "fallback": os.environ.get("GEMINI_SUB_API_KEY", "").strip()}
    if settings:
        data = load(settings)
        if "schema_version" not in data:
            stored = {"primary": data.get("api_key", ""), "fallback": data.get("fallback_api_key", "")}
        else:
            require(data.get("schema_version") == 2, "Unsupported settings schema.")
            entries = {e["id"]: e for e in data["api_configurations"]}
            require(len(entries) == len(data["api_configurations"]), "Duplicate API configuration IDs.")
            stored = {slot: entries[data[name]]["api_key"] if data.get(name) else ""
                      for slot, name in [("primary", "main_api_id"), ("fallback", "sub_api_id")]}
        for slot in keys:
            require(isinstance(stored[slot], str), "Malformed API credential.")
            keys[slot] = keys[slot] or stored[slot].strip()
    return keys


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def count_tokens(model, payload, key, timeout):
    require(isinstance(key, str) and key and all(33 <= ord(c) <= 126 for c in key), "Invalid API credential.")
    url = "https://generativelanguage.googleapis.com/v1beta/models/" + model_name(model) + ":countTokens"
    request = urllib.request.Request(url, data=encode(payload), headers={"Content-Type": "application/json", "x-goog-api-key": key}, method="POST")
    try:
        with urllib.request.build_opener(NoRedirect()).open(request, timeout=timeout) as response:
            result = json.loads(response.read())
        require(isinstance(result, dict), "countTokens returned a non-object response.")
        value = integer(result.get("totalTokens"))
        require(value is not None, "countTokens returned no valid totalTokens value.")
        return value
    except urllib.error.HTTPError as error:
        # Do not save error bodies or exception text: the service may echo a credential.
        status = error.code
        error.close()
        raise MeasurementError("countTokens HTTP " + str(status)) from None
    except (urllib.error.URLError, OSError, socket.timeout):
        raise MeasurementError("countTokens network error or timeout.") from None
    except (json.JSONDecodeError, UnicodeDecodeError):
        raise MeasurementError("countTokens returned invalid JSON.") from None


def measure(directory, keys, timeout=60):
    report = load(directory / "measurement.json")
    require(report.get("method") == METHOD, "Unsupported measurement method.")
    require(digest((directory / "ai-communication.jsonl").read_bytes()) == report["source"]["capture_sha256"], "Archived log was changed.")
    # Validate every prepared request before making any network call.
    payloads = []
    for row in report["requests"]:
        data = (directory / ("count-requests/%03d.json" % row["api_request"])).read_bytes()
        require(digest(data) == row["count_request_sha256"], "A prepared count request was changed; recapture the log.")
        payloads.append(json.loads(data))
        require(row["count_status"] == "measured" or keys.get(row["key_slot"]), "No credential supplied for " + row["key_slot"] + ".")
    failed = False
    for row, payload in zip(report["requests"], payloads):
        if row["count_status"] == "measured":
            continue
        row["measured_at_utc"] = dt.datetime.now(dt.timezone.utc).isoformat()
        try:
            row["count_tokens"] = count_tokens(row["model"], payload, keys[row["key_slot"]], timeout)
            row["count_status"] = "measured"
            row.pop("count_error", None)
        except MeasurementError as error:
            row.update({"count_status": "failed", "count_tokens": None, "count_error": str(error)})
            failed = True
        save_report(directory, report)
        if failed:
            break
    return report, failed


def compare(before, after):
    require(before["method"] == after["method"] == METHOD, "Measurement methods differ.")
    lines = ["| Metric | Before | After | Change |", "|---|---:|---:|---:|"]
    values = [("countTokens reconstructed input", before["totals"]["count_tokens"], after["totals"]["count_tokens"])]
    values += [(k, before["totals"]["api_reported"][k], after["totals"]["api_reported"][k]) for k in USAGE]
    values += [(k, before[k], after[k]) for k in ("api_requests", "query_rounds")]
    for name, a, b in values:
        change = "—" if a is None or b is None else (str(b - a) + (" (%+.1f%%)" % ((b - a) * 100 / a) if a else ""))
        lines.append("| %s | %s | %s | %s |" % (name, "—" if a is None else a, "—" if b is None else b, change))
    requests = lambda r: [q for row in r["requests"] for q in row["retrieval_requests"]]
    lines += ["", "Same displayed question: " + str(before["question"] == after["question"]),
              "Same model sequence: " + str([r["model"] for r in before["requests"]] == [r["model"] for r in after["requests"]]),
              "Same retrieval requests: " + str(requests(before) == requests(after)),
              "Review game snapshot and retrieved evidence too; totals alone do not establish an optimization's effect."]
    return "\n".join(lines) + "\n"


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    p = commands.add_parser("capture", help="Offline: archive a consultation and prepare count requests.")
    p.add_argument("log", type=Path); p.add_argument("--output", type=Path, required=True)
    p.add_argument("--consultation-id"); p.add_argument("--label")
    p = commands.add_parser("measure", help="Online: call countTokens for prepared requests (no generation).")
    p.add_argument("directory", type=Path); p.add_argument("--settings", type=Path)
    p.add_argument("--timeout", type=int, default=60)
    p = commands.add_parser("compare", help="Offline: print a before/after comparison table.")
    p.add_argument("before", type=Path); p.add_argument("after", type=Path)
    args = parser.parse_args(argv)
    try:
        if args.command == "capture":
            report = capture(args.log, args.output, args.consultation_id, args.label)
            print(table(report), end="")
            print("Saved capture: " + str(args.output))
        elif args.command == "measure":
            require(1 <= args.timeout <= 300, "Timeout must be between 1 and 300 seconds.")
            report, failed = measure(args.directory, credentials(args.settings), args.timeout)
            print(table(report), end="")
            for row in report["requests"]:
                if row.get("count_error"):
                    print("API %s: %s" % (row["api_request"], row["count_error"]), file=sys.stderr)
            return int(failed)
        else:
            print(compare(load(args.before / "measurement.json"), load(args.after / "measurement.json")), end="")
    except (MeasurementError, OSError, ValueError, KeyError, TypeError) as error:
        print("Measurement failed: " + str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
