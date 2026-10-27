# AI Advisor extension API

This working tree uses experimental public contract version 3 (not yet released). Public extensions were introduced in AI Advisor v0.14.0. This revision requires explicit command execution modes and splits plan submissions into immediate and reservation phases. Rebuild command and adapter extensions against the matching shipped API DLL; update command definitions and plan-producing integrations as described below. No compatibility overload or old-plan conversion is provided.

**The SDK and public API are under active development and may undergo breaking changes without prior notice. External extensions may require source changes or rebuilding after an update.** Use the API DLL shipped with the host version you are targeting.

## Optional display version

A command or connection adapter can additionally implement `IAdvisorExtensionVersion` to supply its own display version:

```csharp
public string? Version => "0.3.0-beta";
```

This is the version of that individual extension, not its containing MOD, the AI model, or the SDK contract. It is optional: omit the interface or return null, an empty string, or whitespace to hide it. A supplied value must be a single line of at most 80 characters. The host copies it at registration and displays it in **Settings > Extensions**, under **Commands** or **Connection adapters**. It never infers a value from an assembly or MOD, uses it for compatibility checks, or adds it to AI tool definitions. `ApiVersion` remains the separate required contract compatibility value. Built-in commands and adapters explicitly declare their own display versions, independently of the host release version.

The extension lists use the registered command and provider catalogs, including built-in and external entries, and sort each list by case-sensitive ordinal ID order. Viewing an entry does not create an execution session, connect to an API, or read saved credentials. Edit saved connections and their main/sub assignments under **Settings > API connections**.

## Projects and dependencies

`api/SeaPowerAIAdvisor.Api.csproj` builds `SeaPowerAIAdvisor.Api.dll` for .NET Standard 2.1. Its only reference is the standard library: no Unity, Noesis, Anchor Chain, JSON library or provider SDK dependency. Inputs, schemas and results cross the boundary as JSON strings. The host bridge is in `src/Providers/Extensions/`.

Keep `SeaPowerAIAdvisor.Api.dll` next to `SeaPowerAIAdvisor.dll` in the host distribution. Extensions reference the host API DLL with `Private=false` and must not ship duplicate copies. The normal host build packages both DLLs but does not install or enable example extensions.

`examples/ExternalAdapter/` is a separate Anchor Chain mod with two explicitly **offline** examples. Neither makes AI requests. `tests/ExtensionFixture/` is a separate API-only client; it does not reference or link host implementation sources.

## Registration

Register from an Anchor Chain entry point with both a host dependency and after ordering:

```csharp
using AnchorChain;
using SeaPowerAIAdvisor.Api;

[ACPlugin("your.mod.id", "Your AI provider", "1.0.0",
    new string[0], new[] { AdvisorApi.HostModId })]
[ACDependency(AdvisorApi.HostModId, null, null)]
public sealed class Plugin : IAnchorChainMod
{
    public void TriggerEntryPoint() => ProviderRegistration.Register();
}

internal static class ProviderRegistration
{
    internal static void Register()
    {
        if (!AdvisorApi.IsAvailable) return;
        AdvisorApi.Register(new YourProvider());
    }
}

// Implement the chosen execution interface on an internal provider class.
```

Expose only the Anchor Chain entry-point class as `public` in an extension DLL. Keep provider definitions, consultation/session classes and other API-dependent implementation types `internal` or private. The entry point's base types, implemented interfaces, field types and public signatures must not depend on the advisor API. The host ID above is a compile-time constant in the attributes.

Anchor Chain calls `GetExportedTypes()` as it discovers each DLL, before it applies dependency/`after` ordering. Public types implementing advisor interfaces would require the API DLL during discovery and can fail if the extension is scanned first. With this boundary, discovery needs only Anchor Chain; API-dependent registration runs after discovery has loaded the host/API DLLs and the host entry point has initialized the registry. Changing mod-manager order or copying the API DLL into each extension is unnecessary for this pattern.

`IsAvailable` becomes true after the enabled advisor initializes. Registration before that throws. The host checks the directory of the implementation DLL against Sea Power's enabled mods: `Register` returns false for a disabled owner and throws for an unregistered directory. Put that DLL at the root of its registered mod package.

Definitions implement `IAdvisorExtension` and exactly one execution interface below. `ApiVersion` must equal `AdvisorApi.ContractVersion`. IDs are unique and case-sensitive, with up to 80 ASCII letters, digits, dots, underscores or hyphens. Duplicate IDs/field keys and incompatible versions fail explicitly. Definitions should remain immutable after registration. Registration supports adding definitions, with no replacement or unregister operation; restart to reload an implementation. Register during startup before opening settings. Later registrations appear when the editor is reopened.

Gemini, Ollama, OpenRouter and NanoGPT pass through the same catalog registration/validation path and retain their native adapters.

## Settings and connection tests

Declare fields with `ConnectionField`: key, English label, default string, secret flag and optional translated labels. The form supports text and password inputs. Use the `ConnectionField` overload with `numeric: true` for a compact numeric field aligned with the host's numeric settings; the original constructor retains full-width input. `Numeric` is a layout hint only: values remain strings, and the extension owns numeric conversion and validation. Missing translations use the English label.

Providers can optionally also implement `IAdvisorExtensionDescription.GetDescription(string uiLanguage)`. Return a plain-text description of the connection method; the host displays it below provider selection and above all provider-specific fields, updating it when the provider changes. Return an empty string for no description; blank text occupies no display area. Supply English text when a translation is unavailable, and perform no I/O. Both execution interfaces support this optional metadata. This optional interface does not change the adapter execution contract.

For a concise overview in **Settings > Extensions > Connection adapters**, optionally implement `IAdvisorExtensionSummary.GetSummary(string uiLanguage)`. This overview describes what the adapter supports; connection setup instructions belong to `IAdvisorExtensionDescription` instead. Return English for unsupported languages and perform no I/O. An absent summary leaves the card without a description; setup guidance is not substituted. The standard adapters use a short description such as "Connection adapter for the Gemini API."

`ValidateSettings` receives opaque settings JSON and must perform local validation without network I/O or writes. `GetModel` supplies a display/log identifier, never a credential. Fields named `api_key` or `model` are not mandatory. The existing schema 3 stores provider IDs, settings and main/sub assignments. Loading does not rewrite the settings JSON, and an unavailable provider's settings are preserved rather than replaced. At startup, the host renames an existing `gemini.json` to `api-settings.json` only when the new file is absent; if both exist, it uses the new file without changing the old one.

Output limits belong to connection metadata, alongside request deadlines, and are editable for every provider. `ConnectionSettings.MaxOutputTokens` contains the selected connection’s validated positive-integer limit, defaulting to 8192 when absent. It is separate from opaque provider settings and is captured for each connection, including sub connections and pending connection tests. The former shared root-level limit is no longer inherited. Old NanoGPT `settings.max_output_tokens` values are moved to connection metadata when read and are written only in the new location on the next save; reading alone does not rewrite the file. Extensions should apply the supplied limit where their API supports it.

`TestAsync` owns the adapter-specific test sequence; the host does not require a particular number of requests or a tool-calling workflow. The OpenRouter and NanoGPT adapters test their tool round trip with two requests and an isolated dummy result.

`TestAsync` receives only pending connection settings, host connection services, a short test message and cancellation. It receives no game information, history or personality. It does not save/select a connection. Results stay in the settings screen; diagnostic logging follows the saved developer preference.

Provider error history is independent of the optional full communication log. The host stores redacted failure details in `%TEMP%/SeaPowerAIAdvisor/provider-errors.log`, displayed verbatim in Settings → API connections → API errors, where users can select text and copy it with Ctrl+C. Records are appended in chronological order as plain-text blocks containing connection metadata, HTTP status, exception details and the received response body. The body is written directly after credential redaction, without wrapping it in another JSON string. Standard host transport failures are captured automatically, including failed attempts before switching connections. Extensions using their own transport should report received bodies through `BeginRequest`/`EndRequest`, or transport failures through `FailRequest`; headers and credentials must not be passed to these diagnostic methods. HTTP 200 parsing/protocol failures are retained when they reach the host. Clearing chat or restarting the advisor does not clear this history. The enabled mod clears the file once during initialization in each game run. Opening the tab reads the file directly, including externally appended text; no prior provider failure is required. Error-history write failures are surfaced without replacing the original provider error. The existing full communication log remains opt-in at its existing location and retains its existing failure behavior.

## Standard-engine adapter

Implement `IStandardAdvisorExtension` and return `IAdvisorSession` from `OpenSession`. The standard engine supplies an arbitrary list of tool definitions through `ConsultationInput.ToolsJson`. Preserve every supplied definition; no additional opt-in interface is required.

1. `ConsultationInput` separates `DataJson`, `SystemInstruction` and `ToolsJson` (a read-only list of tool-definition JSON strings). Encode every supplied definition for the service API.
2. `Prepare(allowTools)` returns a `ProviderRequest` containing its UTF-8 byte count and deferred send callback. Preparing must not perform I/O. The host checks the sending budget before invoking it.
3. Return final text or `ToolCall` objects normalized to the supplied host tool names and schemas. AI-facing names or wire formats may differ.
4. `AcceptResults` receives matching call IDs/names and result JSON. The adapter owns API history, signatures, continuation IDs and thinking.
5. Dispose session-owned resources; do not dispose shared connection services.

The standard strategy exposes the following tools as applicable:

| Tool | Arguments and purpose |
| --- | --- |
| `query_situation` | In `on_demand` mode, a `requests` array of snapshot queries, with the existing query-specific filters and required fields. |
| `get_situation` | In `on_request` mode, no arguments; retrieve all enabled situation data once. |
| `list_operations` | Optional `search`, `offset`, `limit`; retrieve operation summaries. |
| `describe_operation` | Required `operation_id`; retrieve one definition. |
| `get_operation_candidates` | Required `operation_id`, optional `target_ref`, `offset`, `limit`; retrieve the operation definition together with current eligible targets and choices. |
| `propose_operation_plans` | Required `request_id`, `plans`; submit complete alternative plans together for host selection and approval. Each plan has `plan_id`, `title`, `summary`, `reservation_updates`, `immediate_operations` and `reservation_operations`. Both operation arrays are required; each item has `operation_id`, `target_ref`, `arguments`. |

Operation tools are present only when the accepted consultation permits operations and an operation session is available. Built-in advice/list shortcuts and contact assessments disable them; typed requests retain access to enabled commands. Permission is fixed with the request and survives retries and fallback. With permission off, standard inputs omit operation tools and independent contexts receive no operation service. Situation tools and written advice remain available. Permitted future custom shortcuts can opt in without imposing command execution or changing approval. Their schemas contain their own fields and no `source` or `action` discriminator. Definitions of individual commands are still obtained on demand. Preserve all supplied constraints, including query-specific `anyOf` branches and contact `kind` filtering; the standard strategy does not enable provider-specific strict mode.

Independent read calls in one AI response are normalized into one shared read batch. Each tool result contains a `results` array with zero-based local `index`, `status`, and `data` or `error`; a single invalid item does not discard other successful items. Lookup tools each contribute one read item. A candidate lookup returns `data.operation` with the same definition, `requires_approval` and `plan_policy` as `describe_operation`, alongside the existing `candidates` and pagination fields. It supplies the contract and live choices in one lookup; a separate describe call is only needed for definition-only inspection. Empty and subsequent candidate pages also include the definition. The host retains existing permission, payload, proposal and approval checks. Snapshot evidence is captured at consultation start; candidates reflect their lookup time. The aggregate limit remains eight read items per AI response across all read tools, and forty calls including proposals. Proposal results retain their separate receipt/approval format and ordering.

The per-connection `situation_delivery` setting is `on_demand` by default. Shortcut definitions can override it for one consultation (all current built-ins select upfront, with no per-shortcut UI or persistence); a developer API override takes precedence over the shortcut. Explicit choices apply to both standard main and sub connections, while inheritance uses each connection's own setting. The host resolves these choices before preparing a backend, without changing saved connection settings. Accepted request choices survive retry; operation permission remains independent. In `on_request` mode, the initial data omits automatic situation evidence, including overview, objectives, referenced-unit details and contact-assessment context. User questions, preferences and configured dialogue history remain. The no-argument `get_situation` tool supplies all enabled situation data (`data.situation` and `data.situation_data`, plus deferred mode-specific context) on its first successful call. Later calls return `data.status: "already_supplied"`. The source is the same frozen consultation-start snapshot, not a new capture. Operation lookups alone do not trigger this read. In `upfront` mode, `DataJson.situation_data` contains all enabled snapshot projections without consuming query rounds, and `query_situation` is omitted. Operation lookups and proposals remain available when enabled. `ToolsJson` may therefore be empty. Preserve the supplied list instead of assuming that any particular tool is always present.

Full snapshot export does not consume the partial-query `query_rounds` counter. A `get_situation` call still consumes an API exchange and one of the shared eight read items per AI response. Its complete result must fit the existing 160,000-byte consultation information budget (including initial data and other returned reads); ordinary read responses retain their 48,000-byte ceiling. Rejection or cancellation does not mark the snapshot as supplied. Successful delivery is retained in the host retrieval history for fallback, including independent backends. This avoids a second full tool result; providers that resend conversation history still resend the earlier result as part of that history.

`ConsultationInput` has one constructor accepting `dataJson`, `systemInstruction` and `IEnumerable<string> toolsJson`. Standard adapters always receive the current tool list. The former two-slot properties and tool-format marker interface are not supported. The offline standard example demonstrates the contract.

Independent consultations receive the same `ToolsJson` input container, retain their separate knowledge/operation gateways and control their own retrieval strategy. The host's `upfront` and `on_request` settings are unavailable for them. The independent/standard handoff still preserves the captured knowledge and executed-retrieval history; a standard sub connection applies its own saved delivery choice when no consultation override is present. An unsupported override is rejected during preparation rather than silently ignored or forced onto the independent workflow.

AI operation `candidates` and operations within `submit` plans may use the current consultation's known own-unit alias (`own_unit_N` or `unit:own_unit_N`) as `target_ref`. The host resolves it to the game's `unit:<id>` before candidate filtering and receipt comparison, preserving approval and live validation. Unknown own-unit aliases return `unknown_unit_reference`; numeric and custom target references are not guessed or reinterpreted. A corrected response includes `target_ref_resolution` with `requested`, `resolved` and `reason: "consultation_unit_reference"`; operation history retains the original request. Sonobuoy `arguments.ammunition` also accepts a known consultation `weapon_reference_N` or an exact, unambiguous display name. The command limits matching to its target's carried sonobuoys; a canonical key takes precedence, ambiguous names return `ambiguous_ammunition_reference`, and no approximate match or availability-based substitution is made. Corrections appear in `argument_resolutions` (`argument`, `requested`, `resolved`, `reason`); history retains the original arguments. The optional command argument-normalization contract runs before receipt comparison and proposal storage. Other commands opt in to interpret their own arguments; the host does not assume an ammunition field. Approval revalidates stored canonical IDs, never resolves display names again. Request IDs are unchanged. For a batch, corrections are returned in `plan_resolutions` with `plan_id`, `operation_array` (`immediate_operations` or `reservation_operations`) and zero-based `operation_index` within that array. Independent consultations use this same operation service.

The host retains its existing batching, disclosure validation, retrieval/sending limits, progress and main/sub policy. An empty final reply fails instead of producing an empty successful turn.

## Independent consultation

Implement `IIndependentAdvisorExtension.OpenConsultation` and return `IAdvisorConsultation`. `ConsultAsync` owns its request sequence, encoding, caching, retries and retrieval strategy. It does not use provider sessions or have to use the standard engine. The standard engine's API-request loop limit is not imposed on it.

`ConsultationContext` contains immutable input strings, the knowledge gateway, executed-retrieval history and a progress callback. It contains no live game objects or another connection's credentials. Query the gateway with the supplied schema, omitting `consultation_id`:

```csharp
string result = context.Knowledge.Execute(
    "{\"requests\":[{\"query\":\"catalog\"}]}" );
```

The host supplies the current identity and preserves information selection, target validation, page/batch limits, returned-data limits and retrieval allowance. Excluded, unknown and unavailable information remain distinct. Do not turn a failed retrieval into empty or normal data. The host serializes gateway calls, and rejects calls after the consultation ends.

`ReportProgress` accepts a fixed phase, request count and query names. The host derives retrieval counts from actual usage, masks unknown topic names and labels the active connection. Do not submit model text, entity identifiers or thinking as progress.

Return `AdvisorAnswer`. The host uses actual knowledge-round counts. Leave token usage null when unknown. Honor the cancellation token and dispose owned resources. Cancellation is not a request to switch connections.

## Operation catalog and execution

These additions are available in v0.17.0 and later.

With at least one command enabled, standard adapters receive the four operation tools listed above. Independent consultations receive their knowledge and operation service definitions in `ToolsJson`. Each path uses the same operation service; reading snapshot knowledge never schedules orders. None of the initial schemas embeds every registered operation.

The host owns individual command permissions by operation ID; extensions do not implement their own permission settings. Commands default to enabled, including newly registered IDs, and only disabled IDs are stored. Disabled commands remain inspectable in settings but are omitted from AI discovery and rejected on direct lookup, submission and approval. A mixed plan containing a disabled command is rejected before any of it executes. If every registered command is disabled, the consultation receives no operation service. Changing a permission does not cancel already registered game orders. The obsolete `wip_unit_operations_enabled` setting is ignored and dropped on the next options save.

Independent consultations receive an optional `ConsultationContext.Operations` (`IAdvisorOperations`). If non-null, call `ExecuteAsync(requestJson, cancellation)` with the `action` envelope described by the `game_operations` definition in `Input.ToolsJson`. The host queues detached requests and dispatches on the game thread. No HTTP loop or standard-engine request sequence is required. The service is separate from `IAdvisorKnowledge`: reading snapshot knowledge never schedules orders.

Actions are `list` (optional ID/description search and paging), `describe` (one `operation_id`), `candidates` (live candidates, optional `target_ref` filter and paging), and `submit` (`request_id`, `plans` as above). Read selected definitions before constructing their arguments. Reuse a submission ID only to replay the identical complete set. Each operation definition supplies `plan_policy` (`target`, `minimum`, `maximum`, `selection: "single" | "multiple"`). `target` is the requested generation count, while `minimum` and `maximum` bound accepted submissions; a command's target must lie within its range. The default policy requests and accepts one plan with single selection. For mixed commands, aim for the highest target constrained to their shared accepted range. The host intersects only the accepted ranges and permits multiple selection only if every included command allows it. Valid submissions below target are accepted without requesting or filling in extra alternatives; every submitted plan still undergoes validation. It validates alternatives independently, then revalidates the combined selected orders at approval. Sonobuoy currently requests two alternatives, accepts one or two, and uses single selection. A rejected set retains no partial plan. While the consultation is running, a complete submission with a new `request_id` can replace the last accepted set. The accepted receipt sets `replaces_previous_submission: true` for a revision, explicitly identifying it as a replacement rather than an additional set of alternatives. Validation finishes before the replacement changes any plans or receipts; a rejected revision leaves the prior valid set intact. Reusing an accepted ID with different content returns `request_id_conflict`. An identical retry of the current submission returns its result; an identical retry of an older accepted submission returns `status: "superseded"`, `registered: false` and `current_submission` containing the latest receipt. Old retries never restore old plans. After the consultation finishes, new submissions return `proposal_closed`; identical retries still report their retained status. Failed or cancelled consultations invalidate the latest set as before. Batches allow at most 8 plans and 40 ordered operations per plan, subject to the existing byte and receipt budgets. The host binds battle/turn identity, rechecks command enablement and current applicability, rejects stale or cancelled work, and returns registration receipts. Registration does not mean the game has completed movement or deployment. Built-in reservation commands use `plans[].reservation_updates` to specify one `{ "target_ref", "placement": "append" | "replace" }` per target. Their `reservation_operations` remain ordered and may repeat the same command or interleave movement, drops and formation return for the same target. Sonobuoy arguments now contain only `drops`; movement arguments contain `waypoints`. The array is required and must be empty for plans without composing commands. Placement belongs to the complete target sequence, and replacement clears its shared queue once before its first segment. A single operation uses the same `submit` envelope with one plan and one operation; there is no separate `execute` submission action.

### Immediate execution and reservation registration

`OperationDefinition` requires an `OperationExecutionMode` as its second constructor argument:

```csharp
new OperationDefinition(id, OperationExecutionMode.Reservation, description, parametersJson, localizations);
```

Use `Immediate` for an action performed on adoption, or `Reservation` for registering a game-owned order. The host publishes `execution_mode: "immediate" | "reservation"` in command summaries and definitions, including candidate responses. The submitted array must match this declaration; a mismatch returns `operation_phase_mismatch`, without moving the operation or changing a previously accepted proposal. Both arrays must be present, with `[]` for an unused phase. Together they contain 1–40 operations per plan. The old `operations` field is rejected, including when supplied alongside the new arrays.

All selected work is validated before any action. On adoption, **all immediate operations across the selected plans run first**, then all reservation operations register their orders. Each phase preserves submitted plan order and operation order, regardless of selection click order. The same target or different targets can participate in both phases. Immediate operations cannot be delayed until a queued action completes; that requires a reservation command. Phase labels appear in approval details; target and append/replace displays retain their existing roles. A preflight rejection leaves the proposal pending; an execution failure stops downstream work and uses the existing rejected/partial/unknown result handling. There is no rollback or automatic replay of actions already performed.

Execution mode is independent of shared queue composition. `IAdvisorOperationActions` supports an immediate action or a noncomposing reservation action. An extension implementing `IAdvisorReservationExtension` must declare `Reservation` mode and supply the reservation handler; the host rejects conflicting metadata at registration. Existing built-in movement, sonobuoy and formation-return commands all declare `Reservation`. The external sonobuoy sample also declares `Reservation`, while keeping its noncomposing action handler.

Every session implements `IAdvisorOperationHandler` for candidates and disposal. A noncomposing command also implements `IAdvisorOperationActions` for validation, review text and its post-approval action. These methods remain necessary for arbitrary game actions; they are not an alternative submission endpoint.

Reservation composition is optional. An extension implements `IAdvisorReservationExtension` and its session implements `IAdvisorReservationOperation`: validate all of that command's segments together, describe and preview each segment, then append that segment without clearing the queue. `EndsReservationSequence` marks a terminal command. The host validates policies and terminal order without interpreting command-specific JSON, prepares each target once and executes in submitted order. `ReservationOrder.Index` is zero-based within the target. During preview, `StartPosition` is the preceding planned endpoint; validation/execution receive the original queue start. Return the next endpoint using `ReservationPreview`, or null for a terminal action. A noncomposing reservation command cannot share the same target with a composing reservation command in the selected work because its queue effects are not declared. Immediate commands may use that same target: they execute before reservation preparation, not inside its queue.

Optional `IAdvisorReservationSequenceSupport` inspects the current queue and begins the append/replace sequence. `BeginReservations` returns whether queue/control changed; subsequent rejection must not conceal those mutations. `IAdvisorMovementSupport` lists legal target settings, appends native movement and queues formation return. `MovementUnitInfo.Speeds`, `.Altitudes` and `.Depths` contain immutable `MovementPreset` values (`Id`, `Name`, `Value`, `Unit`). Values are unit-specific targets/references under current conditions, not actual motion: aircraft kt depends on altitude, and submarine depth references depend on native environmental and unit rules. Speed uses `kt`; altitude and positive-below-surface depth use `ft`. `MovementWaypoint.Speed`, `.Altitude` and `.Depth` select preset IDs; altitude and depth are mutually exclusive. An omitted ID preserves the target command. Relative depth is applied as a native preset when the leg activates, never frozen to its previously reported numeric reference. Preset reading and native application belong to the game support layer; commands format this data and validate selected IDs. The built-in movement command exposes these arrays as `speed_options`, `altitude_options` and `depth_options`, each item containing `id`, `name`, `value`, `unit`; waypoint arguments use `speed`, `altitude`, `depth`. Arbitrary numeric settings are not accepted. All are guarded by the existing game-thread and context lifetime checks. Expected `OperationException` failures from queue inspection/preparation cross the same translation boundary as command calls; unexpected exceptions remain faults. `IAdvisorReservationSupport.ClearReservations` remains available. Public values contain no Unity, Noesis, game-object or JSON-library dependencies.

The shared host preflights all selected targets before changes and rechecks on adoption. Command validation aggregates its resource demands across segments. `AmmunitionInfo.Available` excludes queued and active work; `AvailableAfterClearingReservations` excludes active work only, allowing replacement to reclaim commitments it cancels. The existing seven-argument constructor remains conservative. No spent ammunition is restored. Errors after changes use the existing unknown/partial outcome handling, with no automatic rollback or replay. See the [composition design](ai-unit-control-design.md#18-コマンドをまたぐ予約合成と移動編隊復帰2026-10-25) for settings and native constraints.

`RetrievalHistory.OperationsJson` contains executed operation requests/results for a connection handoff. Preserve that evidence so a fallback does not blindly schedule an order again. This remains the provider-facing execution service. AI Advisor v0.18.0 additionally ships the command registration SDK through `AdvisorApi.RegisterOperation` in the contract-version-2 API DLL. See the [command SDK design and implementation status](external-command-sdk-design.md#10-公開契約標準コマンド移行の実装状況). The built-in sonobuoy command uses that same registration/session bridge. The independent [external command example](../examples/ExternalCommand/README.md) has passed registration, AI proposal and direct developer-API reservation/drop checks with unchanged host/API DLLs. In-game approval and preview UI interaction remain unverified.

## HTTP, diagnostics and errors

`IAdvisorConnectionServices.PostJsonAsync` is an optional JSON-object HTTP helper. It applies connection-specific timeout, cancellation, receive limits, error classification and redacted request/response logging. It returns parsed JSON serialized without whitespace. Use an independent transport if exact bytes or another protocol are needed. There is no whole-consultation timeout.

For an independent transport, call `BeginRequest`, then `EndRequest` or `FailRequest` for each actual attempt. The returned ID belongs to that connection service and can be completed only once. IDs are shared with the HTTP helper, and concurrent records retain their sequence. Never submit authentication headers or settings documents to diagnostics. Both selected connections' declared secret values are redacted. Apply the configured request timeout yourself when bypassing the HTTP helper.

Expected service errors use `AdvisorException` with a short lowercase code and optional HTTP status. Do not put raw responses, credentials or explanatory paragraphs in that code. Cancellation uses `OperationCanceledException`. Invariant failures and missing dependencies remain unexpected errors, not temporary-service fallbacks.

## Main/sub handoff

Both strategies use the existing one-way policy. An unhandled eligible temporary error starts the sub with the original common input, executed query/result pairs and actual used/remaining retrieval allowance. It does not transfer private conversation IDs, signatures or thinking, and does not re-fetch facts. A failed sub terminates the consultation; a new consultation starts with main.

Independent implementations can retry internally. Only eligible errors they let escape trigger the common sub policy. Programming/configuration errors and cancellation do not. Total token usage becomes unknown after handoff because a lost response may have consumed tokens.

## Build and validation

With the repository's `Directory.Build.local.props` configured:

```text
dotnet build examples/ExternalAdapter/SeaPowerAIAdvisor.Example.csproj -c Release
```

For a manual test, create a separate enabled mod directory with the example DLL and its `_info.ini`. Use a host containing the API DLL, enable Anchor Chain and AI Advisor, then restart. Register either offline example through API connection settings. The normal host package does not include the example.

The load-order regression check uses the installed game's Mono runtime in isolated processes:

```powershell
dotnet build tests/ExtensionLoading/SeaPowerAIAdvisor.ExtensionLoading.csproj -c Release
python tools/test_extension_loading.py
```

Build the host and example first. The check copies their DLLs into separate temporary directories, with a single API DLL in the host directory. It exercises all six host/API/extension discovery orders using `Assembly.LoadFile`, `GetExportedTypes`, interface/attribute inspection and extension construction, then runs the real extension entry point after host ordering. Registration uses the shipped API and the actual host catalog; the host's Unity initialization and mod-manager activation are not started. It also checks that a host which did not initialize registers no providers. No real settings, game installation or AI endpoint is touched.

Automated tests use real separate client DLLs and mock HTTP. They cover both workflows, settings without Gemini field assumptions, disclosure/round limits, byte budgets, cancellation/disposal, both directions of Gemini handoff, terminal sub failure, programming errors, redaction and concurrent diagnostics. Automated checks do not replace in-game validation of third-party extensions or of each provider/model combination.

Keep the host and shipped API DLL together. Registration rejects a declared `ApiVersion` that differs from `AdvisorApi.ContractVersion`; this check does not guarantee backward compatibility throughout the current experimental development period. Follow the SDK notice above when updating. Removing an extension does not delete its saved connection settings.


### Per-connection instructions

The host stores an optional connection prompt alongside connection metadata, separately from adapter-specific settings. For consultations, it adds the active connection's nonempty prompt to `ConsultationInput.DataJson` as `connection_prompt`, alongside `common_prompt` and `advisor_preferences`. Standard and independent adapters receive the same field without an additional SDK property. The host switches this field when handing off to a sub connection. Existing system rules still apply; connection tests keep their dedicated test instructions.
