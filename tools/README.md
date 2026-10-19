# Developer tools

The [developer local API CLI](advisor-dev.md) operates connections, temporary prompts and consultations through one command-line tool.

## Token measurement tools

`measure_tokens.py` captures and compares AI Advisor consultations without changing the MOD or launching the game. It requires Python 3.10 or later and uses only the standard library.

### Usage

Enable the MOD's full AI communication logging, complete a consultation, then run these commands from the repository root:

```sh
python3 tools/measure_tokens.py capture /path/to/ai-communication.jsonl --output artifacts/token-measurements/before --label "Before"
python3 tools/measure_tokens.py measure artifacts/token-measurements/before --settings /path/to/gemini.json
```

Repeat with a different output directory after testing another version:

```sh
python3 tools/measure_tokens.py capture /path/to/ai-communication.jsonl --output artifacts/token-measurements/after --label "After"
python3 tools/measure_tokens.py measure artifacts/token-measurements/after --settings /path/to/gemini.json
python3 tools/measure_tokens.py compare artifacts/token-measurements/before artifacts/token-measurements/after
```

`capture` and `compare` are offline. `capture` selects the latest advisor consultation, excludes connection tests, and rejects unfinished or incomplete logs. Use `--consultation-id ID` to select a particular consultation. Each capture needs a new output directory.

Only `measure` sends requests to Google's `countTokens` endpoint; it does not generate advice. It uses the model and main/sub key slot recorded for each request. Credentials are read from the explicitly selected local settings file or `GEMINI_API_KEY` / `GEMINI_SUB_API_KEY`; environment variables take precedence. Keys are sent in the authentication header and are not copied into the capture. HTTP redirects are rejected.

A measurement failure stops the run and leaves the count unknown. Run the same `measure` command again to resume; successful counts are retained. Archived log and prepared input hashes are checked before any API call.

### Output

Each capture contains:

- `ai-communication.jsonl`: the selected consultation's original log records.
- `count-requests/`: prepared inputs for individual `countTokens` calls.
- `measurement.json`: counts, original API usage fields, model/key-slot metadata, hashes, and omissions.
- `summary.md`: a readable per-request table and measurement limitations.

Counts remain unknown (`null`) when missing or unsuccessful. The report keeps `countTokens` input counts separate from the API-reported `total_input_tokens`, `total_output_tokens`, `total_thought_tokens`, total, cache, and tool-use values. Captures retain conversation text, game data, and the source log path; store them locally under the ignored `artifacts/` directory.

### Measurement method and limitations

The method identifier is `visible-context-v1`. The script reconstructs a GenerateContent input from the visible Interactions log for each API request:

- Follow `previous_interaction_id` to include the preceding user/model messages and function calls/results. A new interaction root starts a new context.
- Include that request's system instruction and function definitions.
- Preserve input and result text, including any experimental encoded JSON. Function results are represented as text inside a `functionResponse.response.result` object.
- Exclude thought steps and opaque signatures; record their counts in `omitted_context`. Missing history or unsupported content produces an error instead of a partial count.

These values compare reconstructed visible inputs. They do not reproduce hidden server context, internal Interactions framing, or actual billing usage. API-reported usage fields are recorded separately and are not independently validated by this tool. Do not combine the reconstructed input count with reported output/thought counts to claim an exact billed total or price. Attempted requests are included; an attempted input alone does not prove a charge.

For comparisons, use the same save, paused game state, question, model, prompts, information settings, and prior conversation. Inspect the retrieved facts, retrieval failures, and API request counts as well: different model-generated queries can change the totals independently of the optimization being tested. The `compare` output highlights question/model/query differences but does not establish equivalent game states or causality.

API references: [countTokens](https://ai.google.dev/api/tokens) and [GenerateContent](https://ai.google.dev/api/generate-content).

### Offline tests

```sh
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s tools -p 'test_measure_tokens.py' -v
```

The tests use synthetic logs, mock credentials, and mocked HTTP responses. They check context reconstruction, exact text preservation, unavailable history/content, main/sub context boundaries, capture integrity, measurement/resume behavior, unknown values, and credential handling without live API calls.
