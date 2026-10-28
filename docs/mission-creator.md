# Mission Creator foundation

Mission Creator is a separate Anchor Chain MOD in the same repository as AI Advisor. The current prototype adds **AI Mission Creator** beside the mission editor title and opens a movable, resizable game-standard window with settings. Close it with the window header's close button. Opening it again reuses the same window within that editor view; leaving the editor removes its window and restores the original title layout. A reused editor view attaches its launcher again when loaded.

Connection settings, selection and developer API preferences are stored independently of Advisor. AI mission generation and changes to the edited mission are not implemented yet. The launcher remains available across the editor's tabs. The MOD name is always **AI Mission Creator Prototype**. Sea Power reads a name from each language section, so the package repeats this same English name in those sections without translating it. Its description and window labels support English and Japanese; other languages use English.

## Provisional file specification

The [provisional mission-file specification](mission-creator-file-spec.md) records confirmed native behavior, design candidates and open questions for file-based generation and editing. It is a working reference, not a requirement to add abstractions or implement every listed item. Update it when a simpler approach or new evidence changes the design.

## Build and install

Use the same `Directory.Build.local.props` and `DOTNET_EXE` configuration as the repository's main build:

```sh
python3 build.py --target mission-creator
python3 build.py --target all
```

The first command builds only `src/MissionCreator/SeaPowerAIMissionCreator.csproj` and produces `dist/SeaPowerAIMissionCreator`. The second also builds Advisor and runs its offline regression suite. The default target remains Advisor. `--install` copies only the selected packages; stop the game before installing.

Install the MissionCreator package under `Sea Power_Data/StreamingAssets/SeaPowerAIMissionCreator`, enable **AI Mission Creator Prototype** in the game's MOD manager, and restart Sea Power. Anchor Chain is required. AI Advisor and its API DLL are not required.

## Ownership

- `src/MissionCreator/Plugin.cs` owns MOD activation and the editor-construction hook, including an editor instance created before MOD initialization.
- `Editor/MissionCreatorEditor.cs` owns the launch button, header layout and editor load/unload lifecycle. It observes UI events without a per-frame polling loop.
- `UI/MissionCreatorWindow.cs` owns the window, using the editor's existing `WindowHost` and the game's `WindowLayoutNoScroll` template. Like the native editor windows, it loads `Assets/Scripts/Campaign/Window.xaml` into its own resource scope; that template is not assumed to be available on the editor root.
- `UI/MissionCreatorText.cs` and `UI/Localization/` own Creator's labels.
- `Configuration/MissionCreatorSettings.cs` owns Creator's saved connections, selected connection and developer API preferences in `SeaPowerAIMissionCreator/settings.json` under the game's persistent-data directory. It never reads or imports Advisor settings.
- `Connections/MissionCreatorConnections.cs` owns connection tests and their cancellation/results. The UI and developer API call the same service; tests use detached values and do not apply supplemental prompts or send mission data.
- `UI/MissionCreatorConnectionView.cs` composes selection and Creator preferences around the shared cards/editor. Connection selection is inside Settings > API connections and is applied only by its adjacent Save button. Leaving pending selection for another tab, the connection editor, or the main view asks before discarding it. Registration edits save separately; duplicating copies the saved entry, keeps the original selection, and returns to the list.
- `Development/` owns Creator routes, runtime startup/shutdown and discovery. Its HTTP listener implementation is shared; its listener instance, endpoint and test state are independent.
- The project links the required Shared AI, configuration, input, UI and HTTP sources into its own DLL. There is no runtime common DLL or dependency on Advisor or its SDK. Advisor-specific external adapters are not registered in Creator.

UI failures are logged under **AI Mission Creator**, independently of Advisor. Native window presentation and closing remain the game's responsibility. This is not a separate repository, and shared-source changes should be checked against both projects.

## Verification

Build both projects and run the existing Advisor suite before packaging. In the game, check opening, closing, repeated opening without duplicate windows, tab changes, editor exit/re-entry, and operation with AI Advisor disabled. These UI behaviors require native game verification; a successful build alone does not establish them.

## Connection settings and local developer API

The settings screen provides API connection cards and a **Developer** tab. Connections support Gemini, NanoGPT, OpenRouter and Ollama, including each provider's own fields, per-connection timeout/output limit and supplemental prompt. Creator currently selects one connection; connection tests do not use Advisor's main/sub fallback. Supplemental prompts are stored for future generation, and are intentionally excluded from connectivity probes.

The local developer API defaults to **OFF**, uses `http://127.0.0.1:18767`, and can be enabled and assigned a different port from Creator's Developer tab. Advisor remains on `18765`; ControlAPI remains on `18766`. A binding failure is reported in the UI and log without silently choosing another port. The endpoint and process ID are published to `%TEMP%/SeaPowerAIMissionCreator/developer-api.json`; Advisor has its own discovery file.

All Creator requests require `X-MissionCreator-Developer: 1`. POST bodies must be JSON. Discovery and connection listings omit API keys, provider settings and supplemental-prompt text.

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/status` | Last connection test, state, elapsed time, request/token counts or error code |
| GET | `/connections` | Saved connection IDs and safe metadata, in name order |
| POST | `/test` | Start a connection test using optional `connection_id`, otherwise the selected connection |
| POST | `/test/cancel` | Cancel the active connection test |

A test returns HTTP 202 and a `run_id`; poll `/status` for completion. Concurrent tests are rejected with HTTP 409. Each probe makes one or two external AI requests depending on protocol, never saves pending edits, and never falls back to another connection. No generation endpoint or per-request model/prompt override is exposed at this stage.

Example (PowerShell; `/test` sends a real AI request):

```powershell
$d = Get-Content -LiteralPath "$env:TEMP/SeaPowerAIMissionCreator/developer-api.json" -Raw | ConvertFrom-Json
$headers = @{ 'X-MissionCreator-Developer' = '1' }
Invoke-RestMethod -Uri "$($d.endpoint)/connections" -Headers $headers
Invoke-RestMethod -Uri "$($d.endpoint)/test" -Method Post -Headers $headers -ContentType 'application/json' -Body '{}'
Invoke-RestMethod -Uri "$($d.endpoint)/status" -Headers $headers
```

The offline suite checks independent persistence, saved-value duplication/deletion, validation without partial writes, shared UI/API testing, cancellation, provider errors and simultaneous HTTP listeners with separate endpoints/headers. Native layout, focus, confirmation dialogs and editor lifecycle still require game verification.
