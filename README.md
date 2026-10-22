# Sea Power AI Advisor — Prototype

**This is a prototype with limited real-play testing. Advice may be inaccurate, incomplete or unhelpful.**

An in-game operations adviser for **Sea Power: Naval Combat in the Missile Age**. Ask what to do next, discuss tactical risks, or request a more detailed assessment while playing. The adviser uses player-known game information and does not issue game commands or offer unsolicited advice.

[Download releases](https://github.com/mebiustos-sea-power/SeaPowerAIAdvisor/releases) · [Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3821064016) · [Report an issue](https://github.com/mebiustos-sea-power/SeaPowerAIAdvisor/issues)

## Features

- **Gemini, Ollama, OpenRouter and NanoGPT connections.** Register named connections and choose a main connection and an optional sub connection. Duplicate a connection to reuse its settings with another model.
- **On-demand tactical advice.** Ask your own question or use **Next steps**, **Key risks**, **Radar operations**, **Own units** and **Contacts**.
- **Unidentified-contact assessments.** Open an unidentified contact’s information window and click the **[adviser name]’s assessment** button. The adviser evaluates its displayed candidates, confirmed features and relevant known contacts. Buttons below the reply let you reassess assuming a friendly or enemy affiliation.
- **Player-known information only.** Friendly capabilities, sensor operating states and specifications, launched missiles and torpedoes with player-visible target information, aircraft and bases, applicable altitude/depth, movement plans, observed contacts and detection methods, mission briefing, objectives and published reports are available as needed. Hidden enemy state is not supplied.
- **Custom advisers.** Register portraits, names and personality/tone preferences. The displayed title is cosmetic. Shared preferences and your name can also be configured.
- **Readable conversations.** Markdown replies, configurable conversation text size, progress updates and copying of messages. Mouse-wheel scrolling also works over tables in conversation history.
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

Bungalow's Dynamic Campaign Mod is **not required** for battle-screen use. Its campaign screen has optional **alpha** support, disabled by default and enabled in **Settings → General**.

## First use

1. In a mission, open the adviser with **AI** in the bottom-right button row.
2. Open **Settings → API connections → Registrations**, add a named connection and fill in its provider-specific fields.
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
| NanoGPT | API key, subscription-included model ID, output token limit | `z-ai/glm-5.3` |

Choose a model that supports **tool calling**. These are examples, not a guarantee that every model will interpret the supplied data correctly. OpenRouter and NanoGPT connection tests check a tool-call/result round trip as well as the final response.

Future prompt tuning and evaluation of adviser responses will primarily use **GLM 5.3 through NanoGPT**. Other supported services and models remain available, but response quality and information retrieval behavior may vary by model.

NanoGPT connections also have an **Output token limit (including reasoning)** field. Increase it if a model exhausts its reasoning budget before producing an answer or tool call. Existing connections retain their previous effective limit until edited.

Connection settings are stored in `api-settings.json`. On the first startup after this update, an existing `gemini.json` is renamed automatically if the new file does not exist. Its contents are preserved.

NanoGPT uses its subscription-only API for both consultations and connection tests. An active subscription and an included model are required; requests outside the subscription allowance fail without retrying through NanoGPT's pay-as-you-go endpoint. Model names are editable. Any separately configured sub connection still follows the normal fallback policy.

Ollama must be installed and running separately, with the selected model already available. Its default URL is `http://localhost:11434`, and the default context length is **8192**. Performance and memory requirements depend on the model, context length and hardware; the game and model share your computer's resources.

The request timeout is configurable per connection and also applies to connection tests. There is no separate whole-consultation timeout. An eligible rate-limit, temporary server, network or timeout failure can move the consultation to the selected sub connection; it then stays on the sub for that consultation. A new consultation starts on main.

## Information, token usage and response time

By default, the adviser first receives objectives, a situation overview and a catalog of information it can request. It then chooses which details to retrieve, can combine requests, and may ask for further details before answering. **One question can therefore generate several API requests.** Settings let you choose information categories, the retrieval-round limit and how many previous exchanges are included. Data for AI includes a button to restore its default selections. Movement-plan retrieval and sensor operating states are enabled by default; detailed sensor specifications are disabled by default. Movement plans include formation leaders’ routes, formation-following relationships and routes for independently operating units. Existing saved information selections are preserved.

Each registered connection can select **Send all at the start**, **Retrieve as needed**, or **Send all when requested** for situation data. The last option waits for the adviser to request situation data before supplying the full enabled snapshot. Upfront sends all situation data enabled under **Data for AI** from the same captured snapshot. It removes situation-retrieval tools to avoid requesting the same data again; operation lookups and approval remain available. Data exceeding the information budget is rejected before sending. Existing connections default to on-demand delivery. Built-in quick consultations use **Send all at the start** regardless of the connection setting, while typed questions follow the connection setting. **Data for AI** selections still apply. Shortcut-specific delivery is currently internal; user-configurable choices are deferred until custom quick consultations are supported.

Gemini continues with the previous Interaction ID. Ollama, OpenRouter and NanoGPT resend the conversation history with each API request. Neither continuation method guarantees lower input-token usage.

On-demand delivery avoids sending every available detail at the start, but it does **not** guarantee low token usage or lower cost than a single larger request. Large situations, unit capabilities and conversation context can still consume many tokens; earlier context may also be processed during later requests. Retrieval and model processing can make replies take time. Displayed usage depends on the provider's reporting and is not a billing statement.

You supply your own API credentials and are responsible for any service charges. Keys are saved locally in Sea Power's user-data folder and used to authenticate with the selected provider. Questions, included conversation history, adviser preferences and supplied game information go to that provider. With Ollama's default local URL, these requests go to your local server. The mod does not use an author-operated relay service.

The AI receives coordinates, **not terrain or bathymetric map data**. Its geographic assumptions, threat estimates and tactical recommendations may be wrong. Public game-encyclopedia specifications can support estimates for identified classes, but do not reveal an enemy unit's actual ammunition, loadout or condition. Known neutral and destroyed contacts are excluded by default and can be included in **Data for AI**; destroyed contacts are explicitly marked when included.

## Troubleshooting and feedback

For provider failures, open **Settings → API connections → API errors**. The plain-text details can be selected and copied with **Ctrl+C**, including failures before a successful switch to the sub connection. This history remains when chat is cleared or the adviser is restarted, and is cleared when the enabled mod initializes on the next game run. Its file is `%TEMP%/SeaPowerAIAdvisor/provider-errors.log`.

Full request/response JSON logging is a separate developer option, **off by default**. When enabled, it writes to `%USERPROFILE%/AppData/LocalLow/Triassic Games/Sea Power/SeaPowerAIAdvisor/logs/ai-communication.jsonl`. Declared API secrets are redacted, but logs can contain conversation and game information; review them before sharing.

Bug reports are welcome through [GitHub issues](https://github.com/mebiustos-sea-power/SeaPowerAIAdvisor/issues) or the Workshop comments. Include the mod version, provider/model, what you expected, what happened, and relevant error details. Never include an API key.

## For developers

The host ships `SeaPowerAIAdvisor.Api.dll` for external Anchor Chain mods. Extensions can use the standard retrieval engine or implement an independent consultation workflow. See the [public API guide](docs/public-api.md) and [offline example](examples/ExternalAdvisor/Examples.cs). The public API remains experimental.

To build on Windows, install Python 3 and the .NET SDK specified by `global.json`. Copy `Directory.Build.local.props.example` to `Directory.Build.local.props` and set your Sea Power and Anchor Chain directories. The full test script also expects Bungalow's Dynamic Campaign Mod's Workshop files for campaign tests; it remains optional at runtime.

```powershell
python build.py
```

The script builds the mod, runs offline tests with mocked AI communication, and creates `dist/SeaPowerAIAdvisor`. It does not install the package unless `--install` is supplied. Do not distribute user settings, API keys, logs, or the proprietary game/dependency assemblies.

The opt-in [developer local API](docs/developer-local-api.md) supports consultation and temporary prompt experiments against a loaded game situation, using the same UI workflow.

The [token measurement tools](tools/README.md) document separate `countTokens` experiments and their measurement limitations.
