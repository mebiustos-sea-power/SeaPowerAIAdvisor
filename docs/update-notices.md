# Update notices

An optional developer-authored notice appears in a separate game-standard window in front of the main adviser window when the latter is opened. The main adviser window is disabled until the notice closes. The notice title uses the native title bar, and its close button and title-bar X share the same read-receipt path. Normally closing the main adviser window also closes and acknowledges its notice. Merely constructing the window, starting the game, or opening a mission does not acknowledge it. Fault teardown and scene changes do not acknowledge it.

## Preparing a release

`src/UI/Updates/current.json` is embedded in the mod. It contains one current notice with `id`, `title_key`, and `body_key`. Use a new stable ID for a new notice and add the referenced strings to the existing UI translation files. English is required; missing translations use English. Keep an ID unchanged when carrying the same notice into another build or correcting its translation.

For a release without a notice, replace the entire JSON document with `null`. An absent or invalid embedded definition is a build/content error, not a way to disable notices. No version-specific C# branches or history of old notice bodies are needed. Users who skip releases see only the notice in their installed build. Include any still-relevant guidance from skipped releases in that current notice if necessary.

## Body formatting and preview

Notice bodies use the same Markdown renderer as the conversation: headings, emphasis, lists, paragraphs and inline code are supported. Use the shared `update_notice.title` key for the native title bar; its format argument is the installed MOD assembly version, rendered as three components. Topic names belong in body headings. The header uses a green brush scoped to this notice window, retaining the game template and leaving other windows unchanged. Unit/map actions and external-link navigation are not attached to Markdown notice content. A separate localized link below the body opens this MOD's GitHub releases page in the default browser.

With the developer local API enabled and a mission loaded, run `python3 tools/advisor_dev.py notices preview`. This displays the bundled notice even if it has been read and opens the main adviser window if needed. Previewing and closing a preview do not read or write receipt files. Settings, approval, active-consultation and IME-composition states reject the request. A currently open normal notice is not converted into a preview. Missing notice content is reported as `notice_unavailable`.

## Read receipts

Read notice IDs are stored locally in `SeaPowerAIAdvisor/read-notices.json` under the game's persistent data directory. This file is independent of settings, conversations, and mission saves. Receipts survive restarts, language changes, and switching missions. Keeping all read IDs also prevents an older notice from reappearing after a downgrade.

A display alone never writes a receipt. Dismissal reads the latest receipt file and atomically adds the current ID. Read/write failures are logged and shown to the user; a corrupt file is not silently replaced. If persistence fails, the dialog still closes and may appear again later. This mechanism does not change API settings or AI requests.
