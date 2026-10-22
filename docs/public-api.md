# AI Advisor extension API

This is the initial experimental public contract, version 1, introduced in AI Advisor v0.14.0. Earlier releases do not include it.

## Projects and dependencies

`api/SeaPowerAIAdvisor.Api.csproj` builds `SeaPowerAIAdvisor.Api.dll` for .NET Standard 2.1. Its only reference is the standard library: no Unity, Noesis, Anchor Chain, JSON library or provider SDK dependency. Inputs, schemas and results cross the boundary as JSON strings. The host bridge is in `src/Providers/Extensions/`.

Keep `SeaPowerAIAdvisor.Api.dll` next to `SeaPowerAIAdvisor.dll` in the host distribution. Extensions reference the host API DLL with `Private=false` and must not ship duplicate copies. The normal host build packages both DLLs but does not install or enable example extensions.

`examples/ExternalAdvisor/` is a separate Anchor Chain mod with two explicitly **offline** examples. Neither makes AI requests. `tests/ExtensionFixture/` is a separate API-only client; it does not reference or link host implementation sources.

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

Definitions implement `IAdvisorExtension` and exactly one execution interface below. `ApiVersion` must equal `AdvisorApi.ContractVersion`. IDs are unique and case-sensitive, with up to 80 ASCII letters, digits, dots, underscores or hyphens. Duplicate IDs/field keys and incompatible versions fail explicitly. Definitions should remain immutable after registration. Version 1 supports adding registrations, with no replacement or unregister operation; restart to reload an implementation. Register during startup before opening settings. Later registrations appear when the editor is reopened.

Gemini, Ollama, OpenRouter and NanoGPT pass through the same catalog registration/validation path and retain their native adapters.

## Settings and connection tests

Declare fields with `ConnectionField`: key, English label, default string, secret flag and optional translated labels. The current form supports text and password inputs. The extension owns numeric conversion and validation. Missing translations use the English label.

Providers can optionally also implement `IAdvisorExtensionDescription.GetDescription(string uiLanguage)`. Return a plain-text description of the connection method; the host displays it below provider selection and above all provider-specific fields, updating it when the provider changes. Return an empty string for no description; blank text occupies no display area. Supply English text when a translation is unavailable, and perform no I/O. Both execution interfaces support this optional metadata. Existing extensions need no changes and the contract/assembly version remains 1.

`ValidateSettings` receives opaque settings JSON and must perform local validation without network I/O or writes. `GetModel` supplies a display/log identifier, never a credential. Fields named `api_key` or `model` are not mandatory. The existing schema 3 stores provider IDs, settings and main/sub assignments. Loading does not rewrite the settings JSON, and an unavailable provider's settings are preserved rather than replaced. At startup, the host renames an existing `gemini.json` to `api-settings.json` only when the new file is absent; if both exist, it uses the new file without changing the old one.

`TestAsync` owns the adapter-specific test sequence; the host does not require a particular number of requests or a tool-calling workflow. The OpenRouter and NanoGPT adapters test their tool round trip with two requests and an isolated dummy result.

`TestAsync` receives only pending connection settings, host connection services, a short test message and cancellation. It receives no game information, history or personality. It does not save/select a connection. Results stay in the settings screen; diagnostic logging follows the saved developer preference.

Provider error history is independent of the optional full communication log. The host stores redacted failure details in `%TEMP%/SeaPowerAIAdvisor/provider-errors.log`, displayed verbatim in Settings → API connections → API errors, where users can select text and copy it with Ctrl+C. Records are appended in chronological order as plain-text blocks containing connection metadata, HTTP status, exception details and the received response body. The body is written directly after credential redaction, without wrapping it in another JSON string. Standard host transport failures are captured automatically, including failed attempts before switching connections. Extensions using their own transport should report received bodies through `BeginRequest`/`EndRequest`, or transport failures through `FailRequest`; headers and credentials must not be passed to these diagnostic methods. HTTP 200 parsing/protocol failures are retained when they reach the host. Clearing chat or restarting the advisor does not clear this history. The enabled mod clears the file once during initialization in each game run. Opening the tab reads the file directly, including externally appended text; no prior provider failure is required. Error-history write failures are surfaced without replacing the original provider error. The existing full communication log remains opt-in at its existing location and retains its existing failure behavior.

## Standard-engine adapter

For a new adapter, implement `IAdvisorToolListExtension` and return `IAdvisorSession` from its inherited `OpenSession` method. It uses the standard execution strategy with an arbitrary tool list; it adds no methods to implement.

1. `ConsultationInput` separates `DataJson`, `SystemInstruction` and `ToolsJson` (a read-only list of tool-definition JSON strings). Encode every supplied definition for the service API.
2. `Prepare(allowTools)` returns a `ProviderRequest` containing its UTF-8 byte count and deferred send callback. Preparing must not perform I/O. The host checks the sending budget before invoking it.
3. Return final text or `ToolCall` objects normalized to the supplied host tool names and schemas. AI-facing names or wire formats may differ.
4. `AcceptResults` receives matching call IDs/names and result JSON. The adapter owns API history, signatures, continuation IDs and thinking.
5. Dispose session-owned resources; do not dispose shared connection services.

The standard strategy exposes the following tools as applicable:

| Tool | Arguments and purpose |
| --- | --- |
| `query_situation` | A `requests` array of snapshot queries, with the existing query-specific filters and required fields. |
| `list_operations` | Optional `search`, `offset`, `limit`; retrieve operation summaries. |
| `describe_operation` | Required `operation_id`; retrieve one definition. |
| `get_operation_candidates` | Required `operation_id`, optional `target_ref`, `offset`, `limit`; retrieve current eligible targets and choices. |
| `propose_operation` | Required `operation_id`, `target_ref`, `request_id`, `arguments`; submit a proposal through the existing approval service. |

Operation tools are present only with an available WIP operation session. Their schemas contain their own fields and no `source` or `action` discriminator. Definitions of individual commands are still obtained on demand. Preserve all supplied constraints, including query-specific `anyOf` branches and contact `kind` filtering; the standard strategy does not enable provider-specific strict mode.

Independent read calls in one AI response are normalized into one shared read batch. Each tool result contains a `results` array with zero-based local `index`, `status`, and `data` or `error`; a single invalid item does not discard other successful items. Lookup tools each contribute one read item. Snapshot evidence is captured at consultation start; candidates reflect their lookup time. The aggregate limit remains eight read items per AI response across all read tools, and forty calls including proposals. Proposal results retain their separate receipt/approval format and ordering.

The per-connection `situation_delivery` setting is `on_demand` by default. In `upfront` mode, `DataJson.situation_data` contains all enabled snapshot projections without consuming query rounds, and `query_situation` is omitted. Operation lookups and proposals remain available when enabled. `ToolsJson` may therefore be empty. Preserve the supplied list instead of assuming that any particular tool is always present.

Existing `IStandardAdvisorExtension` implementations that do not opt into `IAdvisorToolListExtension` keep their original `ToolJson` (`query_game`) and optional `OperationToolJson` (`game_operations`) schemas. The former batches `source: "situation"` queries and `source: "operation"` lookups with `action: "list" | "describe" | "candidates"`; the latter accepts `action: "execute"` proposals. In upfront mode the legacy read schema contains only operation lookups, or `ToolJson` is null if none remain. Both formats use the same dispatch, limits, ID resolution and approval services; no automatic protocol retry is added. Existing constructors, required interfaces and assembly/contract version 1 remain unchanged. A tool-list input uses `ToolsJson` and leaves the two legacy properties null; `ConsultationInput.WithTools` constructs that input explicitly. The offline standard example demonstrates the new list contract.

Independent consultations keep their original input/gateway and control their own retrieval strategy. The host's upfront setting is unavailable for them. The independent/standard handoff still preserves the captured knowledge and executed-retrieval history; a standard sub connection applies its own saved delivery choice.

AI operation `candidates` and `execute` requests may use the current consultation's known own-unit alias (`own_unit_N` or `unit:own_unit_N`) as `target_ref`. The host resolves it to the game's `unit:<id>` before candidate filtering and receipt comparison, preserving approval and live validation. Unknown own-unit aliases return `unknown_unit_reference`; numeric and custom target references are not guessed or reinterpreted. A corrected response includes `target_ref_resolution` with `requested`, `resolved` and `reason: "consultation_unit_reference"`; operation history retains the original request. Sonobuoy `arguments.ammunition` also accepts a known consultation `weapon_reference_N` or an exact, unambiguous display name. The command limits matching to its target's carried sonobuoys; a canonical key takes precedence, ambiguous names return `ambiguous_ammunition_reference`, and no approximate match or availability-based substitution is made. Corrections appear in `argument_resolutions` (`argument`, `requested`, `resolved`, `reason`); history retains the original arguments. The optional internal argument-normalization contract runs before receipt comparison and proposal storage. Other commands opt in to interpret their own arguments; the host does not assume an ammunition field. Approval revalidates stored canonical IDs, never resolves display names again. Request IDs are unchanged. Independent consultations use this same operation service.

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

## Operation catalog and execution (WIP)

These additions are available in the current development build; the published v0.16.0 does not include them.

With WIP unit scheduling enabled, tool-list standard adapters receive the four operation tools listed above. Legacy standard adapters retain their optional execution-only `OperationToolJson`. Independent consultations receive the original discovery/execution schema in `OperationToolJson`. Each path uses the same operation service; reading snapshot knowledge never schedules orders. None of the initial schemas embeds every registered operation.

Independent consultations receive an optional `ConsultationContext.Operations` (`IAdvisorOperations`). If non-null, call `ExecuteAsync(requestJson, cancellation)` with the same `action` envelope described by `OperationToolJson`. The host queues detached requests and dispatches on the game thread. No HTTP loop or standard-engine request sequence is required. The service is separate from `IAdvisorKnowledge`: reading snapshot knowledge never schedules orders.

Actions are `list` (optional ID/description search and paging), `describe` (one `operation_id`), `candidates` (live candidates, optional `target_ref` filter and paging), and `execute` (`operation_id`, candidate `target_ref`, `request_id`, `arguments`). Read selected definitions before constructing their arguments. Reuse a request ID only to replay the same order. The host binds battle/turn identity, rechecks WIP and current applicability, rejects stale or cancelled work, and returns registration receipts. Registration does not mean the game has completed movement or deployment. The initial operation supports one airborne helicopter traveling to coordinates and dropping one sonobuoy while preserving the player's depth setting.

`RetrievalHistory.OperationsJson` contains executed operation requests/results for a connection handoff. Preserve that evidence so a fallback does not blindly schedule an order again. Existing constructors and required provider interfaces remain available; the optional additions do not require old adapters to implement new members. This is a provider-facing service, not yet a public registration SDK for third-party game operations.

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
dotnet build examples/ExternalAdvisor/SeaPowerAIAdvisor.Example.csproj -c Release
```

For a manual test, create a separate enabled mod directory with the example DLL and its `_info.ini`. Use a host containing the API DLL, enable Anchor Chain and AI Advisor, then restart. Register either offline example through API connection settings. The normal host package does not include the example.

The load-order regression check uses the installed game's Mono runtime in isolated processes:

```powershell
dotnet build tests/ExtensionLoading/SeaPowerAIAdvisor.ExtensionLoading.csproj -c Release
python tools/test_extension_loading.py
```

Build the host and example first. The check copies their DLLs into separate temporary directories, with a single API DLL in the host directory. It exercises all six host/API/extension discovery orders using `Assembly.LoadFile`, `GetExportedTypes`, interface/attribute inspection and extension construction, then runs the real extension entry point after host ordering. Registration uses the shipped API and the actual host catalog; the host's Unity initialization and mod-manager activation are not started. It also checks that a host which did not initialize registers no providers. No real settings, game installation or AI endpoint is touched.

Automated tests use real separate client DLLs and mock HTTP. They cover both workflows, settings without Gemini field assumptions, disclosure/round limits, byte budgets, cancellation/disposal, both directions of Gemini handoff, terminal sub failure, programming errors, redaction and concurrent diagnostics. Automated checks do not replace in-game validation of third-party extensions or of each provider/model combination.

Keep the host and shipped API DLL together. Future compatible releases must preserve existing contract members; breaking changes require a new contract version. Removing an extension does not delete its saved connection settings.
