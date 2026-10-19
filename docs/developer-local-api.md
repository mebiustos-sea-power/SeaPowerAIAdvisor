# Developer local API

The local API drives the same conversation, selected connections, adviser, information policy and consultation workflow as the in-game UI. It is intended for prompt experiments against a loaded game situation. It does not select units, issue orders, load saves or expose API credentials.

## Enable and connect

Open **Settings → For developers → Diagnostics**, enable **Enable developer local API (this PC only)** and save. It is off by default. Its address, or a startup error, appears below the checkbox. The fixed endpoint is:

```text
http://127.0.0.1:18765
```

Close settings before starting a consultation or changing prompts externally. A mission must be loaded and its HUD ready; campaign consultations additionally require the campaign alpha option. A consultation opens the adviser window if needed, and its question and reply appear in the normal transcript. An existing message draft is preserved. Closing the window, changing scenes, or an adviser failure cancels its active work through the normal UI lifecycle.

Every request must include `X-Advisor-Developer: 1`. Send JSON bodies with `Content-Type: application/json`. Browser `Origin` requests are rejected; CORS is not enabled. Only IPv4 loopback is bound. Disabling the option closes the listener and drops its retained diagnostic result. It does not cancel a consultation already running in the UI. A port or permission error is shown and logged; toggle the setting off and on after fixing its cause. Do not expose this endpoint through a network proxy.

## Endpoints

All responses are JSON. These endpoints are experimental and separate from the public provider-extension SDK.

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/v1/status` | Current mode, adviser availability, busy/settings state and last consultation ID. |
| POST | `/v1/consultations` | Start one question or quick consultation; returns HTTP 202 and an ID. |
| GET | `/v1/consultations/{id}` | Read progress, final answer, errors and captured input/retrievals. |
| POST | `/v1/consultations/cancel` | Cancel active work through the same UI path. |
| POST | `/v1/conversation/clear` | Clear the current UI conversation when idle. |
| GET | `/v1/prompts` | All editable prompt fields, using the same `file.json:key` IDs as the temporary prompt editor. |
| PATCH | `/v1/prompts` | Merge supplied text into the temporary prompt overrides. |
| POST | `/v1/prompts/reset` | Restore the prompt bundle loaded at startup. |

Question bodies specify exactly one of `question` and `shortcut`:

```json
{"question":"Which ships can engage the observed aircraft? Check remaining missiles and ranges."}
```

```json
{"shortcut":"key_risks"}
```

`allow_fallback` is optional and **defaults to false** for local-API consultations. The host does not open or use the sub connection unless you explicitly permit it on that request:

```json
{"shortcut":"key_risks","allow_fallback":true}
```

With the default, a main-connection failure ends the consultation instead of switching to a possibly metered sub connection. The saved main/sub assignments remain unchanged. UI-initiated consultations keep their normal fallback behavior. The accepted response and retained result include the effective `allow_fallback` value. A custom consultation backend that cannot honor a no-fallback policy is rejected before execution.

Quick IDs are `next_steps`, `key_risks` and `more_detail`. They use the current request prompts and localized UI captions. `more_detail` requires an answered exchange and enabled conversation history. Questions use the normal 2,000-character limit.

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

## Results and limitations

The consultation response states are `waiting`, `answered`, `failed` and `cancelled`. `progress` contains the latest reported stage, request/round counts, query names, model, connection name and fallback flag. On success, `answer` contains the original Markdown. `result` contains the reported model, connection, request/round counts, query names and nullable `reported_total_tokens`.

`input` captures the provider-independent effective data, system instructions and query tool definition. `retrievals` records the ordered gateway requests and returned data, or a query error code. These are the same detached, player-known snapshots used by the normal workflow; custom backends still choose how to communicate. This is not a raw HTTP transcript and does not record private model reasoning. Provider failure codes and HTTP status are included in `error`; the existing **API errors** page remains the place for provider response details. Token reporting is provider-supplied, not `countTokens` or a billing guarantee. A backend that does not report usage produces `null`.

Only the most recently accepted local-API consultation is retained. Rejected requests do not replace it. Completed results are frozen, so clearing history or retrying a UI turn does not change an earlier result. Starting another accepted local consultation replaces the retained result, and older IDs return HTTP 404. Results are memory-only; save the JSON externally when comparing runs. Manual UI consultations are not captured by this API.

HTTP 409 indicates conditions such as an active consultation, open settings, unavailable game scene, IME composition or an unavailable quick action. Bad payloads return 400. At most 16 commands can wait for the game thread; excess commands return 429. If the game cannot process a command within 15 seconds, it expires with HTTP 503 and is not executed later. This command deadline does not limit the AI consultation duration; the existing per-request provider timeouts still apply. Keep the game rendering frames during experiments.

The transport is exercised with a real Windows loopback HTTP listener in the automated suite. Scene integration, Unity runtime listener behavior and the rendered developer controls still require an in-game check after deployment.
