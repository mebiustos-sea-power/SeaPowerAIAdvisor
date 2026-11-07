# Advisor conversation layouts

The Advisor's consultation screen can load a custom `conversation-layout.json` from the same user data folder as `options.json`:

```text
%USERPROFILE%\AppData\LocalLow\Triassic Games\Sea Power\SeaPowerAIAdvisor\conversation-layout.json
```

Start with the [standard layout](../src/Advisor/UI/Views/conversation-layout.json). To move the sidebar to the right, swap the first and last entries in the first row's `columns` array. The same file applies to tactical battles and the optional campaign integration. Restart the game after editing it. Remove the file to use the embedded standard layout.

Rows run from top to bottom and columns within each row run from left to right. Row `height` and column `width` accept `"Auto"`, `"*"`, positive fixed sizes such as `"150"`, and proportional sizes such as `"2*"`. List the sections in each column in the order they should appear. An empty `sections` list creates a spacer. Use `"scroll": true` to add vertical scrolling to a column such as the standard sidebar.

Include each section exactly once:

- `profile` for the Advisor's portrait, name and activity.
- `shortcuts` for the quick consultation buttons.
- `conversation` for the conversation history and other consultations.
- `composer` for the message input and its buttons.
- `status` for the message at the bottom of the standard window.

The file controls the placement of these sections. Styling and the controls inside each section come from the MOD. If a layout is invalid, the Advisor reports which file and field to correct.
