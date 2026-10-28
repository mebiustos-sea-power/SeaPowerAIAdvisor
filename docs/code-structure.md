# Source layout and ownership

`src/Shared` contains reusable implementation. `src/Advisor` owns the existing AI Advisor feature. `src/Plugin.cs` remains the composition root. Advisor and its Shared sources compile into the existing host DLL. MissionCreator has a separate project and MOD entry point; see [Mission Creator](mission-creator.md). No public Advisor SDK revision is introduced.

```text
src/
  Plugin.cs
  Shared/
    AI/              provider protocols, per-connection sessions, HTTP and neutral connection values
    Configuration/   file read/write primitives without feature-owned paths
    Diagnostics/     secret redaction
    Game/            common game language and mod-activation lookup
    Infrastructure/  embedded dependency loading
    UI/              control resources, XML construction and generic Markdown rendering
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
    UI/              creator window and its own translations
```

## Dependency and state rules

Advisor consumes Shared. Shared does not consume adviser settings, situation readers, command catalogs or UI controllers. Shared's mutable objects belong to a caller or a provider session: moving a class does not make one connection, history, log destination or temporary prompt global across features. Immutable parser metadata and the game's UI resources can be reused.

`AiConnectionSettings` is a detached set of selected provider values, including model, credentials, timeout and output budget. It does not contain the adviser delivery policy, personality, connection-specific prompt or persistence path. Advisor translates its saved configuration at this boundary. Supplemental-instruction placement remains in `AdvisorContext`; the provider receives the completed input without interpreting its purpose. Connection credentials and provider values are never added to AI input automatically.

`IAiTraceObserver` and `IAiErrorObserver` let each caller own its diagnostic destination, redaction and lifecycle. Transport failures remain visible through the existing error behavior; there is no default sink that silently swallows them. Adviser service registration and independent extension workflows remain under Advisor because their public contract includes adviser knowledge and operation services.

`MarkdownRenderer` renders a caller's supplied document and optional reference callback. Advisor's thin renderer owns recognition of unit/weapon/map references. External links and images retain their existing literal rendering. Shared UI resources are applied per view; game resource lookup and hover behavior remain unchanged.

## Configuration and resource locations

Existing adviser settings, profile, templates, notices, logs, packaged prompts, embedded resource IDs, MOD ID and public API names keep their current locations/identities. MissionCreator is an independent MOD in the same repository. It compiles the required Shared sources into its own DLL and does not reference Advisor or its SDK. Its current scope is the editor launcher and an empty window. Future connections, supplemental prompts, settings, history and in-flight work belong to MissionCreator separately; there is no shared saved-connection repository or settings migration.

## Verification

The normal build runs the existing functional regression suite. Its test-project dependency also compiles every Shared source in `tests/SharedCompilation` without any Advisor source or host reference. This compilation is not distributed and prevents feature dependencies from silently entering Shared. Shared-session tests exercise two independent callers with different connection values, instructions and tools. Existing protocol, fallback, logging, configuration, Markdown, input and command checks validate behavior through the moved implementation.
