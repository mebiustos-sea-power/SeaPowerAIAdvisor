# Developer local API

The local API drives the same conversation, selected connections, adviser, information policy and consultation workflow as the in-game UI. It is a temporary investigation interface for experiments against a loaded game situation, not a stable public integration contract. Consultations can request supported native orders when commands are enabled. They do not select map units, load saves or expose API credentials. The direct operation endpoint below exercises the same reservation service without an AI provider.

## Command-line tool

Use the [developer CLI](../tools/advisor-dev.md) for status/connections, temporary prompt changes, consultation, progress and saved results. It accepts arguments and UTF-8 text/JSON files, including explicit connection and fallback choices. It runs outside the MOD and uses only Python's standard library.

## Enable and connect

Open **Settings → For developers → Local API**. Choose the allowed API connections and one developer default, enable **Enable developer local API (this PC only)** and save. Existing installations start with no allowed connections or developer default. It is off by default. Its address, or a startup error, appears below the checkbox. The fixed endpoint is:

```text
http://127.0.0.1:18765
```

Close settings before starting a consultation or changing prompts externally. A mission must be loaded and its HUD ready; campaign consultations additionally require the campaign alpha option. A consultation opens the adviser window if needed, and its question and reply appear in the normal transcript. An existing message draft is preserved. Closing the window, changing scenes, or an adviser failure cancels its active work through the normal UI lifecycle.

Every request must include `X-Advisor-Developer: 1`. Send JSON bodies with `Content-Type: application/json`. Browser `Origin` requests are rejected; CORS is not enabled. Only IPv4 loopback is bound. Disabling the option closes the listener and drops its retained diagnostic result. It does not cancel a consultation already running in the UI. A port or permission error is shown and logged; toggle the setting off and on after fixing its cause. Do not expose this endpoint through a network proxy.

## Temp discovery file

While the local API is enabled and listening, `%TEMP%\SeaPowerAIAdvisor\developer-api.json` contains the endpoint, game `process_id`, `default_connection_id`, `allow_model_override` and the same allowed `connections` metadata as `/v1/connections`. Connection settings and credentials are not included. Read this file to discover choices without making an API request.

Connection lists use the same name order as the settings UI: names are compared case-insensitively, with ordinal ID order breaking ties. Connections are selected by ID, not by list position.

The file is written at startup and after saved option/connection changes, and removed when the local API is disabled or the game exits normally. An abnormal exit can leave it behind; check the recorded process ID before using it. Unchecking or deleting the developer default leaves it unselected rather than choosing another connection automatically.

## Endpoints

All responses are JSON. These endpoints are experimental and separate from the public provider-extension SDK.

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/v1/connections` | Allowed connection IDs, names, providers, models and the developer default. |
| GET | `/v1/status` | Current mode, adviser availability, busy/settings state and last consultation ID. |
| POST | `/v1/consultations` | Start one question or quick consultation; returns HTTP 202 and an ID. |
| GET | `/v1/consultations/{id}` | Read progress, final answer, errors and captured input/retrievals/operations. |
| POST | `/v1/consultations/cancel` | Cancel active work through the same UI path. |
| POST | `/v1/conversation/clear` | Clear the current UI conversation when idle. |
| POST | `/v1/notices/preview` | Show the bundled notice for design checking without reading or writing read receipts; opens the adviser if needed. |
| GET | `/v1/prompts` | All editable prompt fields, using the same `file.json:key` IDs as the temporary prompt editor. |
| PATCH | `/v1/prompts` | Merge supplied text into the temporary prompt overrides. |
| POST | `/v1/prompts/reset` | Restore the prompt bundle loaded at startup. |
| GET | `/v1/operations` | Current battle scope and operation catalog; empty when commands are disabled. |
| GET | `/v1/operations/{operation}/candidates` | Current player-owned candidates and ammunition availability for an operation. |
| POST | `/v1/operation-plans` | Submit and adopt explicit plans without AI or an approval dialog. |
| GET | `/v1/crew/reports` | Reports availability, current scope, targets, appointments, history and visible notification. Does not open the adviser or send AI requests. |
| POST | `/v1/crew/reports/window` | Open or close the Reports window through the HUD button action; unsaved edits still require confirmation. |
| GET | `/v1/crew/reports/situation` | Current player-known input facts for eligible Reports targets; no AI request. |
| PATCH | `/v1/crew/reports` | Save the AI Crew `enabled` flag. Enabling alone does not enable any reporting unit. |
| PATCH | `/v1/crew/reports/targets` | Toggle explicitly listed reporting targets in the current Reports scope; regular scheduling performs generation. |
| POST | `/v1/crew/reports/pause` | Pause or resume Reports while retaining the selected targets, people and history. |
| POST | `/v1/crew/reports/stop` | Stop all Reports targets and cancel pending generation without changing the saved feature flag or session people/history. |

Question bodies specify exactly one of `question` and `shortcut`:

```json
{"question":"Which ships can engage the observed aircraft? Check remaining missiles and ranges."}
```

```json
{"shortcut":"key_risks"}
```

`connection_id` is optional. Omit it to use the developer default; supply an ID from `/v1/connections` to choose another allowed connection:

```json
{"shortcut":"key_risks","connection_id":"api-example","allow_fallback":false}
```

An unset or deleted default returns HTTP 409 (`default_connection_missing`). An unavailable or unapproved connection returns HTTP 403 (`connection_not_allowed`). Developer selections do not rewrite the normal main/sub assignments. The accepted response includes the selected `connection_id`, `connection_name` and effective `model`.

To use another model for one consultation, save **Allow connection model override** (Japanese: **接続モデルの上書き許可**) in the Local API tab, then supply `model`:

```json
{"question":"Assess the current risks.","connection_id":"api-example","model":"vendor/another-model","allow_fallback":false}
```

The permission defaults to **off**, including existing installations. A supplied model without permission returns HTTP 403 (`model_override_not_allowed`). A non-string, empty, over-200-character or control-character model returns HTTP 400 (`invalid_model`); provider validation still applies. Providers must declare a non-secret `model` setting, otherwise the override returns HTTP 400 (`model_override_unavailable`). This does not add a new extension contract or change independent consultation workflows.

The selected connection's credentials, endpoint and other settings are reused in a detached consultation draft. Saved connection fields and main/sub/default selections stay unchanged; omitting `model` uses the registered model on the next request. Progress, diagnostic logs and results identify the model actually used. The override applies only to the selected connection: an explicitly allowed fallback keeps its registered model. Read `allow_model_override` from `/v1/connections` or the Temp discovery file before a model comparison.

`allow_fallback` is optional and **defaults to false** for local-API consultations. The host does not open or use the sub connection unless you explicitly permit it on that request:

```json
{"shortcut":"key_risks","allow_fallback":true}
```

With the default, a selected-connection failure ends the consultation. When explicitly allowed, fallback uses the saved sub connection only if it is also permitted for the local API and differs from the selected connection. The saved main/sub assignments remain unchanged. UI-initiated consultations keep their normal fallback behavior. The accepted response and retained result include the effective `allow_fallback` value. A custom consultation backend that cannot honor a no-fallback policy is rejected before execution.

`allow_operations` is an optional boolean for that consultation. Omit it to follow the request's default: built-in advice/list shortcuts disable operations, while typed questions permit them. Set it explicitly to override that request's choice:

```json
{"shortcut":"key_risks","allow_operations":false,"allow_fallback":false}
```

`false` withholds all operation lookup/proposal tools and the host operation service from standard, legacy and independent backends. Situation retrieval and written advice remain available. `true` permits operation tools only when commands are enabled and the game source allows them; it does not force a command or bypass approval. The accepted response and retained result report the resolved `allow_operations` policy, which is preserved on retry and fallback and is not saved as a global preference. Null/non-boolean values return HTTP 400 `invalid_operation_policy` before execution. Existing direct developer operation/reservation endpoints retain their separate command permissions.

`connection_prompt` optionally replaces the selected connection's saved prompt for this consultation only. Omit it to use the saved prompt; send `"connection_prompt": ""` to omit connection-specific instructions. It uses the same 2000-character limit and validation as the connection editor. This does not change saved settings, subsequent consultations or the sub connection's prompt on fallback, and does not require model-override permission. Null and non-string values are rejected. The effective prompt appears in the existing captured consultation input.

`plain_advisor` is an optional boolean, default **false**. Set it to true to use **Michael Reed with empty personality/tone and empty user-configured Common instructions for this consultation only**:

```json
{"shortcut":"key_risks","plain_advisor":true,"allow_fallback":false}
```

The selected/saved adviser and shared preferences are not modified. The answer and waiting transcript use Michael Reed; the profile sidebar keeps the user's selection. External system instructions and their temporary overrides, game data, language, user name and configured history are retained. Clear history explicitly when a comparison needs it. Acceptance and retained diagnostics include the effective `plain_advisor` flag. It does not affect later UI or local consultations unless requested again.

Quick IDs are `key_risks`, `next_steps`, `radar_operations`, `own_units`, `contacts` and `more_detail`. They use the current request prompts and localized UI captions. `more_detail` remains available through the API and requires an answered exchange and enabled conversation history. Questions use the normal 2,000-character limit.

Notice preview takes an empty object (`{}`), needs no permitted AI connection and makes no provider request. It returns `shown`, `notice_id` and `preview`. Status includes `advisor.window_open`, `advisor.notice_open` and `advisor.notice_preview`. Settings, proposal review, active consultation and IME composition reject the request; an already-open normal notice returns `notice_already_open`. A build without a notice returns `notice_unavailable`. Preview uses the normal localized Markdown window and leaves read receipts unchanged on display and dismissal.

Prompt changes use this form; unknown IDs or invalid values reject the entire change:

```json
{"values":{"requests.json:key_risks":"Recommend actions against the most immediate threats."}}
```

Only the supplied IDs change. Temporary edits and reset do not write prompt files. A running consultation keeps its original prompt snapshot. Both the UI editor and this API share the same override state; external writes are rejected while settings are open. Reset restores the startup bundle, including removal of other temporary edits. These endpoints do not modify the saved Common instructions or adviser personality.

For clear, cancel and reset, send an empty JSON object (`{}`).

## Example in Windows PowerShell

```powershell
$base = 'http://127.0.0.1:18765'
$headers = @{ 'X-Advisor-Developer' = '1' }
Invoke-RestMethod "$base/v1/status" -Headers $headers

# Repeat against the same save and clear history for a comparable experiment.
Invoke-RestMethod "$base/v1/conversation/clear" -Method Post -Headers $headers -ContentType 'application/json' -Body '{}'
$job = Invoke-RestMethod "$base/v1/consultations" -Method Post -Headers $headers -ContentType 'application/json' -Body '{"shortcut":"key_risks"}'
do {
    Start-Sleep -Milliseconds 500
    $result = Invoke-RestMethod "$base$($job.status_url)" -Headers $headers
} while ($result.state -eq 'waiting')
$result.answer
$result | ConvertTo-Json -Depth 50 | Set-Content -Encoding UTF8 '.\advisor-experiment.json'
```

To change one prompt before a new experiment:

```powershell
$change = @{ values = @{ 'requests.json:key_risks' = 'Recommend actions against the most immediate threats.' } } | ConvertTo-Json
Invoke-RestMethod "$base/v1/prompts" -Method Patch -Headers $headers -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($change))
```

These consultation requests use the configured provider and can incur the same charges as clicking a consultation in game. Reading status, prompts and completed results makes no AI request.

## AI-driven reservations

With at least one command enabled under **Settings → Extensions → Commands**, normal and developer-API consultations can use `list_operations`, `describe_operation`, `get_operation_candidates` and `propose_operation_plans`. Individual command permissions apply to both paths; enabling the developer listener is not required for in-game AI operations. The standard situation tool is `query_situation`; its `requests` array batches snapshot queries. Independent read calls across the split tools are still combined within one AI response and share the existing budgets. `propose_operation_plans` submits complete alternatives together (`request_id`, `plans` with `plan_id`, `title`, `summary` and required `immediate_operations`, `reservation_operations`, `reservation_updates`). Each operation declares a generation target, accepted plan-count range and selection policy. The sonobuoy operation requests two alternatives but accepts one or two with single selection; a valid smaller submission needs no retry. Approval revalidates and registers only the selected plan. Normal consultations leave approval to the user; the explicit no-AI development endpoint below adopts only the caller-supplied plan. Standard adapters receive the supplied `ToolsJson` list; independent backends retain the separate knowledge and operation services. Individual operation definitions are not included in the initial input. `get_operation_candidates` returns the selected definition and its plan/approval policy as `data.operation` together with the paged live candidates, so planning does not need a separate `describe_operation` call. Definition-only inspection remains available. Independent candidate lookups receive the same combined data; the direct no-AI candidate endpoint below keeps its existing format.

The retained consultation result includes `input.tools` (the actual prepared definition list) and an `operations` array with request/response pairs. These are separate from read-only snapshot `retrievals`. Operation records use the operation-service action envelope after tool-argument translation; the full communication log retains the original wire calls. Operation candidates and validation use live game-thread state. Sonobuoy scheduling requires own-unit basic information and ammunition information to remain enabled in Data for AI; otherwise it returns `information_disabled`. Disabling commands, cancelling or leaving the battle blocks unregistered queued work. A successful registration remains a game-owned order; it is not a deployment-completion report.

AI `submit` calls return `status: "proposed"` and `registered: false` without changing the game. The host retains the submitted plan set from a successful consultation as a proposal. If any operation requires approval (including sonobuoy scheduling), the player must use **Review proposal… → Adopt** in the conversation window. The command implementation supplies the review text; approval revalidates live conditions and combined inventory. Failed/cancelled consultations and discarded history invalidate unapproved proposals. A custom consultation backend uses this same host-controlled path. The developer API has no approval endpoint. The status response includes `advisor.proposal_review_open`; a new consultation or developer reservation reset is rejected while that review is open.

During AI consultations, operation target aliases `own_unit_N` and `unit:own_unit_N` are resolved only through that consultation's permission-filtered own-unit mapping. Responses include `target_ref_resolution` (`requested`, `resolved`, `reason`) when corrected, so the retained operation request/response pairs and wire logs show both spellings. Unknown own-unit aliases return `unknown_unit_reference` without widening candidate filters. Numeric unit IDs and request IDs are not corrected. The sonobuoy command resolves each `arguments.drops[].ammunition` from a known consultation weapon-reference ID or an exact display name only when it identifies one carried sonobuoy for the target. Canonical keys take precedence; ambiguous names return `ambiguous_ammunition_reference`, while unknown/un-carried values retain normal validation errors. Responses record changes in `argument_resolutions` (`argument`, `requested`, `resolved`, `reason`), alongside the untouched original request. Matching does not choose a different variant based on remaining stock or launch availability. Stored proposals use canonical IDs for receipt comparison and live approval-time validation. Direct developer operation requests below still require canonical target and ammunition identifiers.

Use a capability question such as “What game operations can you perform?” to check discovery without issuing an order. For a controlled execution test, explicitly name an airborne helicopter and coordinates and request one sonobuoy reservation. Normal connection selection and the API's explicit fallback policy still apply.

## Native-order trial (no AI)

Enable the commands used by the plan under **Settings → Extensions → Commands** and save. The developer local API must also be enabled. No AI connection, API key, or developer default connection is needed for these operation endpoints. AI consultation requests use the proposal-and-approval flow above; these explicit developer operation requests register directly for testing.

`seapower.schedule_sonobuoy_drop` registers ordered drops for eligible player-controlled units. It can be combined with `seapower.schedule_movement` and `seapower.schedule_formation_return` in the same plan. Queue policy is set once per target; the host prepares that queue before its first segment, including switching a selected follower to individual movement when supported. Targets are never silently redirected to a flagship. Candidates expose `available`, `unavailable_reason`, the unit's current `position`, `depth` and permitted `reservation_end_position`, and each sonobuoy's internal `id`, `remaining_count`, `available_count`, `available_after_replacement_count` and `single_drop_supported`.

From the repository root:

```sh
python3 tools/advisor_dev.py operations list
python3 tools/advisor_dev.py operations candidates seapower.schedule_sonobuoy_drop --output artifacts/sonobuoy-candidates.json
```

Use the returned `scope_id`, a candidate's `target_ref`, and its actual ammunition `id` in a UTF-8 request file. Choose the destination explicitly. For example, `artifacts/sonobuoy-request.json`:

```json
{
  "scope_id": "COPY_FROM_CURRENT_CANDIDATES",
  "request_id": "sonobuoy-trial-001",
  "plans": [
    {
      "plan_id": "drop",
      "title": "Sonobuoy trial",
      "summary": "One drop at the selected point",
      "reservation_updates": [
        { "target_ref": "unit:REPLACE_WITH_CANDIDATE_ID", "placement": "append" }
      ],
      "immediate_operations": [],
      "reservation_operations": [
        {
          "operation_id": "seapower.schedule_sonobuoy_drop",
          "target_ref": "unit:REPLACE_WITH_CANDIDATE_ID",
          "arguments": {
            "drops": [{ "latitude": 26.5, "longitude": 127.5, "ammunition": "REPLACE_WITH_CARRIED_AMMUNITION_ID" }]
          }
        }
      ]
    }
  ]
}
```

```sh
python3 tools/advisor_dev.py operations submit --request-file artifacts/sonobuoy-request.json --output artifacts/sonobuoy-result.json
```

Each plan sets `reservation_updates[].placement` to `append` to preserve a target's existing reservations, or `replace` to clear its entire queue once before the first new segment. Repeated operations for that target share the same policy. Replacement does not affect other units. `available_after_replacement_count` excludes active ammunition use while allowing cancelled reservations to be reused. The direct developer endpoint still requires the same complete argument format and does not normalize AI aliases.

Candidate and registration-result `depth` is read-only information: `above_layer`, `below_layer`, or `alternating` (the game's Intermittent setting). The first release always uses the player-controlled game setting. A supplied `arguments.depth` is ignored without changing the setting or rejecting an otherwise valid reservation, and is not advertised as an operation parameter. The host reads the current setting again when registering the order; a player's change while the AI is responding is preserved. Other unsupported arguments and invalid reservation data are still rejected.

The result reports the setting observed at registration, not a depth fixed to that reservation. The game uses the unit-wide setting at the actual drop, including a later UI change. Identical request replays return the original receipt without modifying current settings. The native bridge contains no depth-setting write or execution hook.

The short operation description uses `battle.json:operation_sonobuoy_drop` in the existing prompt bundle. It belongs to the operation catalog; normal consultations do not receive additional system instructions. A depth-control parameter can be added later as an explicit extension; none is active in the first release.

Both `placement` and `drops` are required. A unit plan contains 1–40 drop points; each alternative can contain at most 40 sonobuoy points in total across its units. Coordinates are decimal degrees, latitude −90…90 and longitude −180…180. The game controls approach, altitude adjustments and release tolerance; the reservation does not promise an exact stationary release at that point. Each drop point registers one round; launchers that only support multi-round salvos are rejected.

A successful response has `status: "registered"`. It acknowledges registration, not travel, release or deployment completion. Inspect the game's waypoint display and observe movement/drop in the actual game. The MOD does not monitor or announce completion.

Keep the same request file/ID when checking a lost acknowledgement: an identical repeated request returns the retained result without registering again. Reusing an ID with changed arguments returns `request_id_conflict`. A new intended operation requires a new ID. A changed battle scope returns `operation_scope_changed`; fetch new candidates after loading a save or mission. The CLI never automatically retries or substitutes a new scope. Receipts live in memory for the loaded battle, with a limit of 1,024; the host rejects further new requests instead of evicting receipts and permitting duplicate orders.

Disabling commands rejects new registrations, including already queued local-API requests. It preserves orders registered in the game. A condition rejection returns HTTP 409 with `status: "rejected"` and a reason `code`; disabled scheduling returns HTTP 403 (`unit_operations_disabled`). Unexpected implementation errors are logged through the local API's existing error path. Their request IDs remain unresolved (`operation_outcome_unknown` on retry) because an exception does not prove that no order was registered. Inspect the game before deliberately submitting a replacement request.

These operation contracts are an internal prototype, separate from the public provider-extension SDK. There is no operation-progress monitor, completion callback or automatic follow-up consultation.

## Inspect and clear native reservations (developer only)

These endpoints inspect the current native queue of a player-controllable own unit. Commands must be enabled. They are separate from the AI operation catalog and do not call an AI provider or change the selected map unit.

| Method | Path | Result |
| --- | --- | --- |
| GET | `/v1/units/{unit_id}/reservations` | Current battle `scope_id`, unit identity, `count` and ordered `reservations`. |
| POST | `/v1/units/{unit_id}/reservations/clear` | Remove every queued reservation of this unit, including manual orders; return `removed_count`, remaining `count` and queue. |

`unit_id` is the game's numeric unit ID, as supplied by a badge or operation candidate, rather than a consultation-local `own_unit_N`. For a reset, send exactly `{"scope_id":"<scope from the current reservation list>"}`. A stale battle scope is rejected. A missing/uncontrollable unit, disabled commands, active consultation or open settings also rejects the reset; no confirmation dialog or automatic retry is used.

Each reservation includes its `sequence`, native `task_type`, `kind` and applicable `position`. Attack/sonobuoy reservations also include `ammunition`, `count` and `attack_position`. Other task kinds remain in the list. Relative waypoints and destinations calculated at execution time have a null position rather than an invented geographic point. Full native objects are not serialized.

The reset uses the game's `RemoveWaypoints()` path and verifies the queue is empty. It does not restore consumed ammunition or undo already launched/deployed weapons, and it is not a general cancellation of firing work already handed off by a reservation. Use a paused game with unexecuted reservations for repeatable prompt experiments. Existing AI request receipts remain registration history; use a new consultation for a new trial.

```sh
python3 tools/advisor_dev.py reservations list --unit-id 1029 --output /tmp/hobby-reservations.json
python3 tools/advisor_dev.py reservations clear --unit-id 1029 --scope-id <scope_id_from_list>
```

## Results and limitations

The consultation response states are `waiting`, `answered`, `incomplete`, `failed` and `cancelled`. `progress` contains the latest reported stage, request/round counts, query names, model, connection name and fallback flag. On success, `answer` contains the original Markdown. `result` contains the reported model, connection, request/round counts, query names and nullable `reported_total_tokens`.

An `incomplete` result has no final `answer`. The standard engine stops without an additional tools-disabled request when its exchange or information-size budget is exhausted. `result.completion_reason` is `round_limit` or `payload_limit` (`completed` for normal answers). Unapproved proposals from that consultation expire; previously registered game orders remain. Limits do not trigger an automatic retry or fallback. Independent backends retain their own communication workflow.

`input` captures the effective data, system instructions and `tools` definition list for the selected adapter and delivery mode. Independent runs retain their original input schemas. `retrievals` records the ordered gateway requests and returned data, or a query error code. These are the same detached, player-known snapshots used by the normal workflow; custom backends still choose how to communicate. This is not a raw HTTP transcript and does not record private model reasoning. Provider failure codes and HTTP status are included in `error`; the existing **API errors** page remains the place for provider response details. Token reporting is provider-supplied, not `countTokens` or a billing guarantee. A backend that does not report usage produces `null`.

Only the most recently accepted local-API consultation is retained. Rejected requests do not replace it. Completed results are frozen, so clearing history or retrying a UI turn does not change an earlier result. Starting another accepted local consultation replaces the retained result, and older IDs return HTTP 404. Results are memory-only; save the JSON externally when comparing runs. Manual UI consultations are not captured by this API.

HTTP 409 indicates conditions such as an active consultation, open settings, unavailable game scene, IME composition or an unavailable quick action. Bad payloads return 400. At most 16 commands can wait for the game thread; excess commands return 429. If the game cannot process a command within 15 seconds, it expires with HTTP 503 and is not executed later. This command deadline does not limit the AI consultation duration; the existing per-request provider timeouts still apply. Keep the game rendering frames during experiments.

The transport is exercised with a real Windows loopback HTTP listener in the automated suite. Scene integration, Unity runtime listener behavior and the rendered developer controls still require an in-game check after deployment.

## Per-consultation situation delivery

`POST /v1/consultations` accepts optional `situation_delivery`: `on_demand` (retrieve as needed), `upfront` (send all permitted situation data at the start), or `on_request` (send all permitted situation data on the first `get_situation` request). It uses the selected allowed connection's credentials without changing saved settings. Resolution order is an explicit API override, the shortcut's internally defined delivery choice, then the selected connection's saved delivery. All current built-in shortcuts use `upfront`; typed questions without an override inherit connection settings. There is no shortcut delivery setting in the UI or options file. No additional permission switch is required; the allowed connection and Data for AI restrictions still apply. Model-override permission remains separate. An explicit API or shortcut choice applies to both standard main and allowed sub connections for that consultation. Without either override, each connection retains its own saved delivery.

```json
{"shortcut":"key_risks","situation_delivery":"upfront","allow_fallback":false}
```

Null, non-string and unknown values return HTTP 400 `invalid_situation_delivery` before consultation execution. Independent workflows retain their own strategy; an unsupported `upfront` or `on_request` choice returns HTTP 400 `situation_delivery_unavailable`. The accepted response and retained result report the selected connection's captured `situation_delivery`. The retained result also includes `situation_delivery_override` (null means connection inheritance); actual input records the mode in use, including after fallback. Preparation is deferred until the shortcut policy is resolved, and unsupported choices are rejected before starting a turn. `/v1/connections` and the Temp discovery file report each connection's saved value; this field is null for independent workflows. These choices do not change normal UI consultations or saved defaults. The result's recorded input also identifies the actual `interaction_mode`.

For `on_request`, initial `input.data` excludes automatically attached situation context, including overview and objectives. The first successful full result appears once in `retrievals` with `query: "get_situation"`; repeated requests report `already_supplied` without copying the full result again. Data remains captured at consultation start and respects Data for AI selections. `query_rounds` counts partial retrievals, so it remains zero for this full export; `api_requests` still counts all actual API exchanges. Operation reads/proposals remain separately recorded.

### Composed native-order trial (no AI)

`POST /v1/operation-plans` accepts an explicit `scope_id`, `request_id`, `plans` and optional `selected_plan_ids`. Use the same [ordered plan and target policy format](ai-unit-control-design.md#18-コマンドをまたぐ予約合成と移動編隊復帰2026-10-25) as AI submissions. Unlike a normal consultation, this development request adopts the selected plan immediately without a dialog. Command enablement, ownership, live preflight, terminal-order checks and replay protection still apply. Consultations, settings and proposal review must be idle. Successful registration is HTTP 200; a pending/rejected result is 409. Reusing an ID with different contents or a different completed selection is a conflict. The CLI exposes this as `operations submit --request-file FILE`; it never retries a POST automatically. There is no separate single-operation submission route; one plan with one operation uses the same endpoint.

## AI Crew Reports verification

Reports uses the same opt-in listener and main-thread queue. Its generation is independent of a tactical adviser conversation. The saved Reports connection must also be in the developer API's allowed connection list before the API can turn a target ON. Reports does not use the adviser's fallback assignment.

First read `GET /v1/crew/reports` and use its `scope_id` and `targets[].id`. For example:

```json
{"scope_id":"<current Reports scope>","target_ids":["unit:1001","unit:1029"],"enabled":true}
```

Send this to `PATCH /v1/crew/reports/targets`. IDs may include formation followers. Every selected unit retains its crew and ON/OFF state when formations change; `targets[].can_report` identifies current formation leaders and standalone units. Followers can be appointed but cannot speak until they become eligible. The entire requested set is validated before any target changes. Old scope IDs are rejected after a battle session changes. Requests do not bypass the normal coalescing, batching, appointment or notification pipeline. Stop automatic reporting after a bounded test with `POST /v1/crew/reports/stop` and the same scope ID.

A generation considers at most four speech candidates and may return remarks for zero to two of them. New appointments are returned independently, so silent crew still receive names and personalities. Appointment-only requests contain no situation data and do not accelerate the speech interval. Silence is a successful result and does not necessarily append history. Candidates left silent are not immediately retried; the next opportunity follows the whole-Crew interval. Use the busy state and communication trace when verifying a bounded generation rather than waiting only for history growth.

CLI examples:

```sh
python3 tools/advisor_dev.py reports status
python3 tools/advisor_dev.py reports situation
python3 tools/advisor_dev.py reports targets --scope-id <scope> --target-id unit:1001 --enabled true
python3 tools/advisor_dev.py reports pause --scope-id <scope> --value true
python3 tools/advisor_dev.py reports pause --scope-id <scope> --value false
python3 tools/advisor_dev.py reports stop --scope-id <scope>
```

`PATCH /v1/crew/reports` with `{"enabled":false}` exercises the saved global disable path: it hides Crew UI, cancels reports and turns targets OFF. Re-enabling does not resume those targets. `notification` in the status response reports the current visible text, placement and native UI bounds; it does not fabricate a report for display testing.

Reports target API requests are explicit commits; they do not edit the window’s pending checkbox selection. `POST /v1/crew/reports/pause` takes `{"scope_id":"...","paused":true}` or `false`. While paused, confirmed targets remain selected, but neither appointments nor remarks are generated. Resume validates the selected connection permission again. The global disable and `/stop` still turn all targets OFF.
