# Sea Power AI Advisor — Prototype

**This is a prototype with limited real-play testing. Advice may be inaccurate, incomplete or unhelpful.**

An in-game operations adviser for **Sea Power: Naval Combat in the Missile Age**. Ask what to do next, discuss tactical risks, or request a more detailed assessment while playing. The adviser uses player-known game information and can propose supported game commands for you to review and approve. It does not offer unsolicited advice.

[Download releases](https://github.com/mebiustos-sea-power/SeaPowerAIAdvisor/releases) · [Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3821064016) · [Report an issue](https://github.com/mebiustos-sea-power/SeaPowerAIAdvisor/issues)

## What's new in v0.18.0

Specify destinations directly from the map and request plans that combine movement, sonobuoy deployment and formation return. Review and adopt a proposal before its orders are added to the game.

- **Location badges:** Ctrl+left-click empty map space while AI Advisor is open to attach a point to your message.
- **Movement and formation return:** plan routes using unit-specific speed, altitude and depth presets, combine different operations and units, and review `Append` / `Replace` choices before adoption.
- **Consult while hidden:** consultations continue when the window is hidden; the AI button shows a yellow dot while consulting and a green dot when a result is ready.
- **Connection-specific prompts:** configure supplementary instructions for individual connections or models.
- **System text templates:** use Weapon employment or your own saved messages, with replacement or append insertion.
- **External command SDK:** add commands without changing the host; built-in commands use the same public contract.

**When updating:**

- **NanoGPT:** we strongly recommend **DeepSeek V4.1 Flash 1017 (non-thinking)**, model ID `deepseek/deepseek-v4.1-flash-1017`. Existing saved connection models are unchanged.
- **Command settings:** commands default to enabled, including newly added ones; the former global OFF setting is not carried over.
- **External extensions:** extensions using the previous public API contract require updates and rebuilding for contract version 2.

See the [v0.18.0 release notes](https://github.com/mebiustos-sea-power/SeaPowerAIAdvisor/releases/tag/v0.18.0) for all changes.

## Features

- **Gemini, Ollama, OpenRouter and NanoGPT connections.** Register named connections and choose a main connection and an optional sub connection. Duplicate a connection to reuse its settings with another model.
- **On-demand tactical advice.** Ask your own question or use **Next steps**, **Key risks**, **Radar operations**, **Own units** and **Contacts**.
- **AI-assisted game commands.** Request movement, sonobuoy deployment and formation return, including combined plans for one or more units. Review and approve a proposal before its orders are added to the game.
- **Map location badges.** Ctrl+left-click empty map space while the conversation is open to attach destinations or drop locations to your question. Click draft badges to locate their units or points on the map.
- **Consultations continue while hidden.** A yellow dot on the AI button means a consultation is running; green means a result is ready. Both dots are hidden while the adviser window is open.
- **Alternative proposals with map previews.** Compare planned locations and travel routes, choose one proposal to adopt, and retain its number and title in conversation history.
- **Text templates.** Choose a built-in message or register your own reusable messages in settings, then select them through **Text templates**. Edit the message and attach unit or location badges before sending.
- **Unidentified-contact assessments.** Open an unidentified contact’s information window and click the **[adviser name]’s assessment** button. The adviser evaluates its displayed candidates, confirmed features and relevant known contacts. Buttons below the reply let you reassess assuming a friendly or enemy affiliation.
- **Player-known information only.** Friendly capabilities (including fixed-wing economic cruise altitude and speed), sensor operating states and specifications, launched missiles and torpedoes with player-visible target information, aircraft and bases, applicable altitude/depth, movement plans, observed contacts and detection methods, mission briefing, objectives and published reports are available as needed. Currently displayed mission circles are included with basic mission information, using their labels, center coordinates and radii. Hidden enemy state is not supplied.
- **Custom advisers.** Register portraits, names and personality/tone preferences. The displayed title is cosmetic. Shared preferences and your name can also be configured.
- **Readable conversations.** Markdown replies, configurable conversation text size, approximate response times in seconds, progress updates that reflect operation lookups and proposal receipt, and copying of messages. Mouse-wheel scrolling also works over tables in conversation history.
- **Unit-specific questions.** Use **Insert selected unit** or **Ctrl+left-click** a unit link in conversation history to add a removable target badge to your message. Copied questions include the unit references.
- **Unit navigation.** Left-click a linked unit name or contact number to select and center it on the map; right-click to open the game's standard menu for that unit. Friendly-unit and contact links can be enabled separately in settings.
- **Weapon references.** Right-click a weapon link in an answer and choose **Weapon Reference** from the menu to open the game's reference window.
- **Coordinate links.** Left-click a position link to center the map on that location and display a marker for approximately three seconds.

The English and Japanese UI follows the game language, with English as the fallback. The AI is separately instructed to answer in the game's selected language.

## Installation

Requires Sea Power and a working [Anchor Chain installation](https://steamcommunity.com/sharedfiles/filedetails/?id=3380210757). Follow Anchor Chain's installation instructions; subscribing to its Workshop item alone is not the complete installation.

Choose one installation method for AI Advisor:

- **Workshop:** subscribe to the [AI Advisor item](https://steamcommunity.com/sharedfiles/filedetails/?id=3821064016).
- **Manual:** download `SeaPowerAIAdvisor-vX.Y.Z.zip` from a [release](https://github.com/mebiustos-sea-power/SeaPowerAIAdvisor/releases) and extract the included `SeaPowerAIAdvisor` folder into `Sea Power_Data/StreamingAssets/`. Use the attached distribution ZIP, rather than GitHub's generated source archives.

Enable AI Advisor in the game's Mod Manager, then completely restart Sea Power. Use either the Workshop copy or the manual copy to avoid loading duplicate installations. Keep the package's DLLs and `Prompts` folder together when updating.

Bungalow's Dynamic Campaign Mod is **not required** for battle-screen use. Its campaign screen has optional **alpha** support, disabled by default and enabled in **Settings → WIP (experimental)**.

## First use

1. In a mission, open the adviser with **AI** in the bottom-right button row.
2. Open **Settings → API connections → Registrations**, add a named connection and fill in its provider-specific fields. Saved connections appear as cards with their provider, model and main/sub assignment; select a card to edit it.
3. Use **Test connection**, then save the registration. A connection test does not save or select it.
4. In **Selection**, choose the main connection and optionally a different sub connection, then save.
5. Return to chat and send a message or select a quick action. **Enter** sends; **Shift+Enter** inserts a new line.

For an unidentified contact, open its information window and use **[adviser name]'s assessment**. This button is unavailable when the contact is already identified or another applicable restriction prevents assessment. An assessment is an inference and does not change the game's classification.

### Connection options

| Provider | Settings | Model example |
| --- | --- | --- |
| Gemini | API key, Gemini model ID | `gemini-3.5-flash-lite` |
| Ollama | Server URL, installed model, context length | `qwen3:8b` |
| OpenRouter | API key, model ID in `provider/model` form | `google/gemini-3.5-flash-lite` |
| NanoGPT | API key, subscription-included model ID | `deepseek/deepseek-v4.1-flash-1017` |

Choose a model that supports **tool calling**. These are examples, not a guarantee that every model will interpret the supplied data correctly. OpenRouter and NanoGPT connection tests check a tool-call/result round trip as well as the final response.

For NanoGPT, we strongly recommend **DeepSeek V4.1 Flash 1017 (non-thinking)**, the primary model used for prompt tuning and evaluation of adviser responses. Other supported services and models remain available, but response quality and information retrieval behavior may vary by model.

Each connection can also have an optional **Connection prompt** (up to 2000 characters), added to consultations using that connection. It supplements the common prompt and takes precedence when the two conflict; a switch to the sub connection uses that connection’s instructions. Connection tests use their dedicated test instructions.

Duplicating a connection immediately saves a separate copy of its saved settings and returns to the card list. Pending edits require confirmation before being discarded. The original connection keeps its main/sub assignments; copies are unassigned. Duplicate names are allowed.

**Save** applies to the current tab’s settings or the individual registration being edited. Leaving a settings tab with pending changes asks whether to continue editing or discard them; switching tabs does not save changes.

Each connection has an **Output token limit (including reasoning)** field, defaulting to **8192**. Increase it if a model exhausts its output budget before producing an answer or complete tool call. The limit is applied to each API response, including connection tests. Previously saved NanoGPT connection limits are retained; connections without their own limit use the new default rather than the retired shared setting.

Connection settings are stored in `api-settings.json`. An existing `gemini.json` is renamed automatically if `api-settings.json` does not exist. Its contents are preserved.

NanoGPT uses its subscription-only API for both consultations and connection tests. An active subscription and an included model are required; requests outside the subscription allowance fail without retrying through NanoGPT's pay-as-you-go endpoint. Model names are editable. Any separately configured sub connection still follows the normal fallback policy.

Ollama must be installed and running separately, with the selected model already available. Its default URL is `http://localhost:11434`, and the default context length is **8192**. Performance and memory requirements depend on the model, context length and hardware; the game and model share your computer's resources.

The request timeout is configurable per connection and also applies to connection tests. There is no separate whole-consultation timeout. An eligible rate-limit, temporary server, network or timeout failure can move the consultation to the selected sub connection; it then stays on the sub for that consultation. A new consultation starts on main.

## Game commands

Open **Settings → Extensions → Commands** to enable or disable commands and browse those registered for the current screen. Details show their identifiers, requirements, approval policy, current AI instructions and definition JSON. Shared tool definitions are also available. Each command has its own checkbox. Commands, including newly added ones, default to enabled; only individually disabled command IDs are saved. The former global command switch is ignored and removed the next time options are saved. Browsing does not send an AI request or inspect live candidates. The **Connection adapters** tab lists registered built-in and external connection types; edit saved connections under **API connections**. Each extension can optionally supply its own display version. Unspecified versions are not shown.

The built-in commands support:

- **Movement reservations:** set ordered waypoints with the unit's available speed, altitude and depth presets. Settings take effect when travel toward the corresponding waypoint begins; omitted settings remain unchanged. A following unit switches to independent action, while moving a formation leader preserves the game's normal follower behavior.
- **Sonobuoy drop reservations:** schedule drops at multiple points using airborne friendly aircraft carrying supported sonobuoys. The aircraft's current sonobuoy depth setting is preserved.
- **Formation return:** return an eligible unit to its existing formation after earlier reservations finish.

For example, ask “Build an effective antisubmarine screen along California's route.” Attach unit and location badges to specify the units and points to use, or let the adviser find suitable candidates. One proposal can combine different commands and units; the same unit can move, deploy sonobuoys and then return to formation. Built-in quick consultations request advice; use a typed message or text template to request operations.

Before any orders are added, open the proposal review window. Compare planned locations and travel routes on the map, and switch between proposals when alternatives are available. Commands specify their proposal-count policies; sonobuoy plans aim for two alternatives and accept one valid proposal when a meaningful alternative is unavailable. Choose one proposal and select **Adopt** to approve it. Its number and title remain in conversation history. During a consultation, the AI can revise its submitted proposal set before the consultation finishes.

For each unit, the adviser chooses whether to append its plan to existing reservations or replace that unit's entire existing reservation queue, following any explicit constraints in your request. The approval window shows `Append` / `Replace` badges, and the map preview starts at the corresponding route origin. The game handles queued movement and deployment; these features do not continuously monitor or replan execution.

Hiding the adviser does not cancel an active consultation. While the window is hidden, a yellow dot on the AI button indicates an ongoing consultation and green indicates a result is ready. Reopen the adviser to read it, or use the consultation's stop button to cancel an active request.

## Information, token usage and response time

By default, the adviser first receives objectives, a situation overview and a catalog of information it can request. It then chooses which details to retrieve, can combine requests, and may ask for further details before answering. **One question can therefore generate several API requests.** Settings let you choose information categories, the API request limit per consultation (**15 by default**) and how many previous exchanges are included. Existing saved request limits are preserved. If a consultation reaches the limit, a message explains how to increase it in settings. Data for AI includes a button to restore its default selections. Movement-plan retrieval, sensor operating states and detailed sensor specifications are enabled by default. Movement plans include formation leaders’ routes, formation-following relationships and routes for independently operating units. Existing saved information selections are preserved.

Each registered connection can select **Send all at the start**, **Retrieve as needed**, or **Send all when requested** for situation data. The last option waits for the adviser to request situation data before supplying the full enabled snapshot. Upfront sends all situation data enabled under **Data for AI** from the same captured snapshot. It removes situation-retrieval tools to avoid requesting the same data again; operation lookups and approval remain available. Data exceeding the information budget is rejected before sending. Existing connections default to on-demand delivery. Built-in quick consultations use **Send all at the start** regardless of the connection setting, while typed questions follow the connection setting. **Data for AI** selections still apply. Text templates follow the same connection settings as a typed message.

**Text templates** switches the conversation area to a selection screen. Press it again to return to the conversation. The built-in Weapon employment template asks about the specified unit's weapons, suitable targets and when to conserve ammunition. Attach a unit badge before sending. User templates are messages you add, edit or remove under **Settings → Text templates**. The same settings page lets you hide system templates and choose whether selection replaces the message or appends it after a line break. System templates are shown by default, and selection defaults to replacement. Both types keep attached unit and location badges and do not send a consultation when selected. Edit the text and send normally, using the same operation permissions and situation-delivery setting as a typed question.

Gemini continues with the previous Interaction ID. Ollama, OpenRouter and NanoGPT resend the conversation history with each API request. Neither continuation method guarantees lower input-token usage.

On-demand delivery avoids sending every available detail at the start, but it does **not** guarantee low token usage or lower cost than a single larger request. Large situations, unit capabilities and conversation context can still consume many tokens; earlier context may also be processed during later requests. Retrieval and model processing can make replies take time. Displayed usage depends on the provider's reporting and is not a billing statement.

You supply your own API credentials and are responsible for any service charges. Keys are saved locally in Sea Power's user-data folder and used to authenticate with the selected provider. Questions, included conversation history, adviser preferences and supplied game information go to that provider. With Ollama's default local URL, these requests go to your local server. The mod does not use an author-operated relay service.

The AI receives coordinates, **not terrain or bathymetric map data**. Its geographic assumptions, threat estimates and tactical recommendations may be wrong. Public game-encyclopedia specifications can support estimates for identified classes, but do not reveal an enemy unit's actual ammunition, loadout or condition. Known neutral and destroyed contacts are excluded by default and can be included in **Data for AI**; destroyed contacts are explicitly marked when included.

## Troubleshooting and feedback

For provider failures, open **Settings → API connections → API errors**. The plain-text details can be selected and copied with **Ctrl+C**, including failures before a successful switch to the sub connection. This history remains when chat is cleared or the adviser is restarted, and is cleared when the enabled mod initializes on the next game run. Its file is `%TEMP%/SeaPowerAIAdvisor/provider-errors.log`.

Full request/response JSON logging is a separate developer option, **off by default**. When enabled, it writes to `%USERPROFILE%/AppData/LocalLow/Triassic Games/Sea Power/SeaPowerAIAdvisor/logs/ai-communication.jsonl`. Declared API secrets are redacted, but logs can contain conversation and game information; review them before sharing.

Bug reports are welcome through [GitHub issues](https://github.com/mebiustos-sea-power/SeaPowerAIAdvisor/issues) or the Workshop comments. Include the mod version, provider/model, what you expected, what happened, and relevant error details. Never include an API key.

## For developers

The host ships `SeaPowerAIAdvisor.Api.dll` for external Anchor Chain mods. The current SDK contract is version 2; adapter inputs use the `ToolsJson` list exclusively. Extensions can use the standard retrieval engine or implement an independent consultation workflow. Register custom commands without rebuilding the host; built-in commands use the same public command contract. See the [public API guide](docs/public-api.md), [external command example and build guide](examples/ExternalCommand/README.md), and [offline connection adapter example](examples/ExternalAdapter/Examples.cs). The SDK and public API are experimental and may undergo breaking changes without prior notice; external extensions may require updates or rebuilding. See the [SDK stability notice](docs/public-api.md). Developer-authored update announcements are configured as described in the [update notice guide](docs/update-notices.md).

To build on Windows, install Python 3 and the .NET SDK specified by `global.json`. Copy `Directory.Build.local.props.example` to `Directory.Build.local.props` and set your Sea Power and Anchor Chain directories. The full test script also expects Bungalow's Dynamic Campaign Mod's Workshop files for campaign tests; it remains optional at runtime.

```powershell
python build.py
```

The script builds the mod, runs offline tests with mocked AI communication, and creates `dist/SeaPowerAIAdvisor`. It does not install the package unless `--install` is supplied. Do not distribute user settings, API keys, logs, or the proprietary game/dependency assemblies.

The opt-in [developer local API](docs/developer-local-api.md) supports consultation and temporary prompt experiments against a loaded game situation, using the same UI workflow.

The [token measurement tools](tools/README.md) document separate `countTokens` experiments and their measurement limitations.
