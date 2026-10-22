# Developer local API CLI

`advisor_dev.py` operates the existing [developer local API](../docs/developer-local-api.md). It requires Python 3.10+ and only the standard library. It runs outside the game; no MOD build or installation is needed. Enable the local API and configure its permitted/default connections in the game first.

From the repository root:

```sh
python3 tools/advisor_dev.py --help
python3 tools/advisor_dev.py discover
python3 tools/advisor_dev.py status
python3 tools/advisor_dev.py connections
```

On Windows use `py -3` or your Python executable instead of `python3`. On WSL, `auto` transport uses Windows PowerShell to reach the Windows loopback listener. On Windows/native Linux it uses direct HTTP. The script discovers Windows Temp dynamically; there are no user-specific paths or connection IDs in the code.

## Options and commands

Global options go **before** the command:

| Option | Meaning |
| --- | --- |
| `--discovery-file FILE` | Select a different public discovery JSON; the default is `%TEMP%/SeaPowerAIAdvisor/developer-api.json`. |
| `--endpoint URL` | Use an explicit `http://127.0.0.1:PORT` address instead of reading discovery. |
| `--transport auto\|direct\|powershell` | Override the local HTTP transport. |
| `--powershell EXE` | Select the Windows PowerShell executable. |
| `--timeout SECONDS` | Timeout per local HTTP request, default 20 seconds. This is not an AI provider timeout. |
| `--quiet` | Suppress stdout while retaining saved files and stderr errors. |

Each command has its own `--help`:

| Command | Operation |
| --- | --- |
| `discover` | Read public Temp metadata without accessing the HTTP API. |
| `status` | Read current game/adviser state. |
| `connections` | Read currently permitted connection IDs and the developer default. |
| `prompts get [--id ID]` | Fetch the full temporary prompt snapshot, or one field's exact text. |
| `prompts set` | Apply text/file/batch input to temporary prompts. |
| `prompts reset` | Explicitly restore all prompts loaded at game startup. |
| `conversation clear` | Explicitly clear conversation history. |
| `consult` | Submit exactly one consultation and normally wait for its result. |
| `result ID [--wait]` | Fetch or continue waiting for an existing consultation; never submit it again. |
| `cancel` | Explicitly cancel active consultation work. |
| `operations list` | Read the WIP operation catalog and current battle scope. |
| `operations candidates OPERATION` | Read current own-unit candidates and available ammunition. |
| `operations execute --request-file FILE` | Submit one explicit reservation request without AI; preserves the supplied scope and request ID. |
| `reservations list --unit-id ID` | Inspect this own unit’s current native reservations in queue order. |
| `reservations clear --unit-id ID --scope-id SCOPE` | Remove all queued reservations of this unit, including manual orders. |

All commands except `consult` accept `--output FILE` to save the response as UTF-8 JSON. `prompts get --id` saves raw UTF-8 text, preserving leading/trailing newlines. `consult --output DIRECTORY` creates a **new** result directory; an existing directory is rejected before any HTTP operation. Without `--quiet`, responses also go to stdout. Errors go to stderr and return nonzero. An incomplete/failed/cancelled consultation also returns nonzero while preserving its result JSON.

## WIP operation trial

Operation commands require **Settings → WIP (experimental) → Enable unit order scheduling**, as well as the local API switch. They need no AI connection and make no provider requests. See [native-order trial](../docs/developer-local-api.md#wip-native-order-trial-no-ai) for the request format, limitations and confirmation procedure.

```sh
python3 tools/advisor_dev.py operations list
python3 tools/advisor_dev.py operations candidates seapower.schedule_sonobuoy_drop --output artifacts/candidates.json
python3 tools/advisor_dev.py operations execute --request-file artifacts/order.json --output artifacts/order-result.json
```

The request file must explicitly contain its current `scope_id`, `request_id`, operation, target and arguments. Sonobuoy candidates and registration results report the player-controlled `depth` (`above_layer`, `below_layer`, or `alternating`). The first release does not advertise depth control and ignores any supplied `arguments.depth`, preserving a player's setting change while the AI is responding. The tool does not generate new identities, rebind stale targets, or retry a POST. A `registered` result only acknowledges a native reservation; it does not wait for the helicopter or sonobuoy.

For repeated paused-game trials, `reservations list` returns the numeric unit ID, current battle scope, order sequence and sonobuoy coordinates/ammunition/count. Use that scope explicitly with `reservations clear` to reset the unit before the next consultation. Clearing uses the game's native removal method and verifies the queue is empty; it does not undo consumed ammunition or deployment already underway. Both commands require WIP order scheduling. Clear is rejected while a consultation or settings editing is active, never retries automatically and does not infer a different battle scope. These are developer commands, not AI-advertised operations.

```sh
python3 tools/advisor_dev.py reservations list --unit-id 1029 --output /tmp/queue.json
python3 tools/advisor_dev.py reservations clear --unit-id 1029 --scope-id <scope_id_from_queue>
```

## Questions, connection and recording

```sh
python3 tools/advisor_dev.py --quiet consult --shortcut key_risks --output artifacts/prompt-tests/risk-01
python3 tools/advisor_dev.py --quiet consult --question-file artifacts/question.txt --connection-id api-example --no-fallback --clear-history --output artifacts/prompt-tests/question-01
```

Choose exactly one of `--question TEXT`, `--question-file FILE`, `--shortcut ID`, or `--request-file FILE`. Files are UTF-8, optionally with a BOM. Quick IDs are `key_risks`, `next_steps`, `radar_operations`, and `more_detail`.

Omitting `--connection-id` uses the saved **developer default**, not the normal main assignment. Fallback defaults to **false**; use `--allow-fallback` to permit it explicitly or `--no-fallback` to override a request file. History is preserved unless `--clear-history` is supplied. Each `consult` starts one consultation; its internal provider requests remain controlled by the MOD.

Use `--model MODEL_ID` for a per-consultation model override after enabling **Allow connection model override** in the Local API settings. The selected connection supplies credentials and its other settings; neither its registered model nor the normal/default connection selections change. The option requires an allowed connection, leaves an allowed fallback's model unchanged, and never enables fallback itself. Omitting it uses the saved model. `--model` overrides a request file's `model` value; that field is also accepted directly in JSON. `discover` and `connections` report `allow_model_override` without credentials.

```sh
python3 tools/advisor_dev.py --quiet consult --shortcut key_risks --connection-id api-example --model vendor/another-model --no-fallback --clear-history --output artifacts/model-tests/another-01
```

A request file can specify the full current consultation body:

```json
{
  "question": "Which ships should engage the observed aircraft? Check ammunition and ranges.",
  "connection_id": "api-example",
  "allow_fallback": false
}
```

```sh
python3 tools/advisor_dev.py consult --request-file artifacts/request.json --output artifacts/prompt-tests/file-01
```

`--plain-advisor` uses Michael Reed with no personality/tone or user-configured Common instructions for that consultation. The saved adviser/shared instructions remain intact. System prompt files and their temporary edits still apply; user name, language, information settings and history are retained. Combine it with `--clear-history` for a comparison without prior dialogue:

```sh
python3 tools/advisor_dev.py --quiet consult --shortcut key_risks --plain-advisor --clear-history --output artifacts/prompt-tests/plain-01
```

The same option can be supplied as `"plain_advisor": true` in a request file. `--no-plain-advisor` overrides that file back to the selected adviser. Omitting both uses the request file's value, or normal configured preferences when the field is absent. The MOD must include support for this new developer API option.

Explicit connection/fallback/plain-adviser flags override their fields in the file. The tool uses registered game connections; it does not read API-key settings or authenticate directly with providers.

Result directories contain `request.json`, `status-before.json`, `accepted.json`, `latest.json`, changed `progress.jsonl` entries, the complete `result.json` (including separate operation request/receipt records when WIP is used), and `answer.md` when answered. Explicit history clearing also saves `clear-response.json`; operational failures save `error.txt`. The final JSON retains the API's captured effective input, retrievals, progress and reported usage without rewriting their content.

By default, polling runs every 0.8 seconds with no overall wait deadline. Configure `--poll-interval SECONDS`, optional `--wait-timeout SECONDS`, and `--progress` to print changed progress to stderr. `--answer-only` prints only the final Markdown reply.

```sh
python3 tools/advisor_dev.py consult --shortcut next_steps --no-wait --output artifacts/prompt-tests/async-01
python3 tools/advisor_dev.py result CONSULTATION_ID --wait --output artifacts/resumed-result.json --answer-file artifacts/resumed-answer.md --progress-file artifacts/resumed-progress.jsonl
```

`--no-wait` returns the accepted ID immediately. A wait deadline or Ctrl+C stops this client only and does not cancel the adviser. Resume using `result ID --wait`, or use `cancel` explicitly. The tool never automatically retries a consultation POST: if its acknowledgement is lost, inspect `status` for `last_consultation_id` rather than submit another question. Polling itself does not call an AI provider. The API retains only its latest accepted consultation, so save each result before starting another.

## Prompt experiments without code edits

```sh
python3 tools/advisor_dev.py --quiet prompts get --output artifacts/prompts-before.json
python3 tools/advisor_dev.py --quiet prompts get --id requests.json:key_risks --output artifacts/key-risks-before.txt
python3 tools/advisor_dev.py --quiet prompts set --id requests.json:key_risks --file artifacts/key-risks-candidate.txt
python3 tools/advisor_dev.py --quiet consult --shortcut key_risks --clear-history --output artifacts/prompt-tests/candidate-01
python3 tools/advisor_dev.py --quiet prompts set --id requests.json:key_risks --file artifacts/key-risks-before.txt
```

Use `--text TEXT` instead of `--file` for a short value (`--text ""` clears it). Batch edits use a JSON input file:

```json
{
  "values": {
    "common.json:system": "You are a military operations adviser within the world of Sea Power.",
    "requests.json:key_risks": "Recommend concrete actions against the most urgent threats."
  }
}
```

```sh
python3 tools/advisor_dev.py prompts set --changes-file artifacts/prompt-changes.json
python3 tools/advisor_dev.py prompts set --changes-file artifacts/prompts-before.json
```

`--changes-file` also accepts an unedited full `prompts get` snapshot, making complete restoration possible through the same command. Restoring one field preserves other edits. Restoring a full snapshot or running `prompts reset` affects all its fields; the tool does not automatically restore prompts or clear history. Changes are temporary and use the existing API rules for active consultations/settings. No prompt source files are modified.

## Local mock tests

```sh
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s tools -p 'test_advisor_dev.py' -v
```

The tests run a loopback mock server and use synthetic connections, prompts, answers and failures. They exercise Unicode/file input, request selection, fallback flags, prompt snapshots, progress/result preservation, timeout/resume, errors, and no duplicate POST after a lost acknowledgement. No real AI is called. The optional Windows bridge integration test starts a separate Windows mock listener:

```sh
ADVISOR_TEST_POWERSHELL='/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe' PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s tools -p 'test_advisor_dev.py' -v
```

## Compare situation delivery

Use `consult --situation-delivery on_demand` or `consult --situation-delivery upfront` for a single consultation. Omitting the flag uses the selected connection's saved choice. Request files accept the same `situation_delivery` strings; an explicit flag takes precedence. The selected connection's credentials, model and Data for AI restrictions are preserved, and no saved setting is modified. Fallback remains off unless explicitly enabled, and any allowed fallback keeps its own delivery choice. Invalid file values are rejected before API access or history clearing.

```sh
python tools/advisor_dev.py consult --shortcut key_risks --situation-delivery upfront --no-fallback --plain-advisor --clear-history --output results/upfront
python tools/advisor_dev.py consult --shortcut key_risks --situation-delivery on_demand --no-fallback --plain-advisor --clear-history --output results/on-demand
```

Use the same save, connection/model and information selections for a comparison. `connections`/`discover` show saved delivery modes, while `accepted.json` records the requested consultation's effective choice; verify actual input in `result.json`. Each command starts a real consultation, so run it only when authorized.
