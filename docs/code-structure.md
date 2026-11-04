# Source layout and ownership

`src/Shared` contains reusable implementation. `src/Advisor` owns the existing AI Advisor feature. `src/Plugin.cs` remains the composition root. Advisor and its Shared sources compile into the existing host DLL. MissionCreator has a separate project and MOD entry point; see [Mission Creator](mission-creator.md). No public Advisor SDK revision is introduced.

```text
src/
  Plugin.cs
  Shared/
    AI/              provider protocols/definitions, per-connection sessions, HTTP, neutral profiles and connection tests
    Configuration/   file read/write primitives without feature-owned paths
    Diagnostics/     secret redaction and host-supplied logging destinations
    Game/            common game language and mod-activation lookup
    Infrastructure/  embedded dependency loading
    UI/              shared connection cards/editor, tab navigation, controls and Markdown
    Development/     loopback HTTP listener and response/error types without feature ports
  Advisor/
    Configuration/   saved connections, own-unit disclosure, prompts and user preferences
    Consultation/    advice workflow, conversation, tool dispatch and situation delivery
    Providers/       adviser service metadata, connection checks and public-extension bridging
    Diagnostics/     adviser traces, error history and runtime lifecycle
    Prompts/         adviser prompt loading and temporary edits
    Game/            battle/campaign capture and integrations
    Operations/      tactical commands and approval/execution
    Development/     adviser developer API
    Notifications/   adviser notices and read receipts
    UI/              adviser windows, settings, transcript and game-reference interactions
  MissionCreator/
    Plugin.cs        independent Anchor Chain entry point
    Editor/          mission editor button and view lifecycle
    Configuration/   Creator-only saved connections, selection and developer preferences
    Connections/     detached connection tests and cancellation shared by UI/API
    Development/     Creator routes, port, discovery and listener lifecycle
    UI/              creator window/settings and its own translations
```

## Dependency and state rules

Advisor consumes Shared. Shared does not consume adviser settings, situation readers, command catalogs or UI controllers. Shared's mutable objects belong to a caller or a provider session: moving a class does not make one connection, history, log destination or temporary prompt global across features. Immutable parser metadata and the game's UI resources can be reused.

`AiConnectionSettings` is a detached set of selected provider values, including model, credentials, timeout and output budget. It does not contain the adviser delivery policy, personality, connection-specific prompt or persistence path. Advisor translates its saved configuration at this boundary. Supplemental-instruction placement remains in `AdvisorContext`; the provider receives the completed input without interpreting its purpose. Connection credentials and provider values are never added to AI input automatically.

`IAiTraceObserver` and `IAiErrorObserver` let each caller own its diagnostic destination, redaction and lifecycle. Transport failures remain visible through the existing error behavior; there is no default sink that silently swallows them. Adviser service registration and independent extension workflows remain under Advisor because their public contract includes adviser knowledge and operation services.

`MarkdownRenderer` renders a caller's supplied document and optional reference callback. Advisor's thin renderer owns recognition of unit/weapon/map references. External links and images retain their existing literal rendering. Shared UI resources are applied per view; game resource lookup and hover behavior remain unchanged.

## Configuration and resource locations

Existing adviser settings, profile, templates, notices, logs, packaged prompts, embedded resource IDs, MOD ID and public API names keep their current locations/identities. MissionCreator is an independent MOD in the same repository. It compiles the required Shared sources into its own DLL and does not reference Advisor or its SDK. Its current scope is the editor launcher, connection settings and a local connection-test API. Connections, supplemental prompts, settings and in-flight tests belong to MissionCreator separately; there is no shared saved-connection repository or settings migration.

## Verification

The normal build runs the existing functional regression suite. Its test-project dependency also compiles every Shared source in `tests/SharedCompilation` without any Advisor source or host reference. This compilation is not distributed and prevents feature dependencies from silently entering Shared. Shared-session tests exercise two independent callers with different connection values, instructions and tools. Existing protocol, fallback, logging, configuration, Markdown, input and command checks validate behavior through the moved implementation.

## Connection management and developer HTTP boundary

`AiConnectionProfile`/`AiConnectionDraft` hold neutral saved/editable values, including supplemental instruction text without deciding where it appears in AI input. `AiProviderDefinition` owns built-in field declarations, validation, factories and protocol-specific connection tests. Advisor's service bridge supplies Advisor test instructions/results and retains public external-adapter registration; Creator consumes the built-in definitions directly.

`AiConnectionsView` and `Connections.xaml` own cards, generic field editing, provider drafts, dirty checks and save/copy/delete interactions. `IAiConnectionStore` delegates persistence to the feature. Advisor's optional delivery editor implements `IAiConnectionEditorOptions`; the shared form does not contain battle policy or an Advisor/Creator switch. Role labels and localization are supplied by the host. Game typing ownership, deferred automatic focus and settings-tab navigation are shared game/UI responsibilities.

`LocalApiServer` requires an explicit endpoint and developer request header. Advisor owns `18765` and its existing routes/discovery; Creator owns configurable `18767` and separate routes/discovery; ControlAPI remains separate on `18766`. Mutable server instances, configuration files, credentials and tasks are never shared. Creator's current routes only access detached connection configuration and network tests; any future route that accesses native editor objects must establish its own game-thread boundary.

## Embedded save-data compatibility

The native save bridge owns lifecycle timing and opaque payload storage. The MOD owns its envelope; feature owners own their data and any feature migrations. Format versions are independent of the MOD release number. Normal readers consume only their current format.

Each owner declares a supported minimum and current format. Migrations advance one version at a time on a detached document. Supporting a format does not promise permanent compatibility: retiring the oldest format raises the minimum and removes that format's migration and dedicated tests without changing later steps or the current reader. Unreleased experiments do not require migration support.

Absent MOD data starts normally without a notice. A recognized but retired format starts with initial MOD state, allows play and subsequent saves, and produces one notice per load once the battle UI is available. Loading does not rewrite the source save; the next save contains current state, without the unrestored data. Malformed data, newer formats and broken or missing migrations remain errors, retain diagnostics and block saving unrestored MOD state when that data is included. They must not be classified as routine retirement.

The MOD save gateway interprets the save-inclusion setting and selects capture work. Shared receives only optional deferred work, without accessing settings or knowing individual features. Omitted work writes no MOD section; selected work runs only after successful restoration. This permits explicit native-only saving after a MOD restoration failure without concealing the error. The setting controls writes only: existing embedded data still restores, and changing the setting preserves in-memory feature state. The initial setting is ON.

Public mission-message history is owned by its game feature and stored as a versioned entry in the MOD envelope. It retains displayed text and publication times, independently of permission to send messages to the AI. Restoring a save merges the archive with publications observed during loading; it does not re-run triggers or show the saved popups again. Matching text/time occurrences are reconciled during restoration to avoid multiplying replayed popups while preserving repeated publications. Missing archives do not reconstruct events from before the load. Feature-format retirement identifies the affected history in the existing load notice.
