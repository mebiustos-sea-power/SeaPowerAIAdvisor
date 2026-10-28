# Mission Creator foundation

Mission Creator is a separate Anchor Chain MOD in the same repository as AI Advisor. The current prototype adds **AI Mission Creator** beside the mission editor title and opens an empty, movable, resizable game-standard window. Close it with the window header's close button. Opening it again reuses the same window within that editor view; leaving the editor removes its window and restores the original title layout. A reused editor view attaches its launcher again when loaded.

AI generation, connection settings, saved state and changes to the edited mission are not implemented in this foundation. The launcher remains available across the editor's tabs. The MOD name is always **AI Mission Creator Prototype**. Sea Power reads a name from each language section, so the package repeats this same English name in those sections without translating it. Its description and window labels support English and Japanese; other languages use English.

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
- `UI/MissionCreatorWindow.cs` owns the empty window, using the editor's existing `WindowHost` and the game's `WindowLayoutNoScroll` template. Like the native editor windows, it loads `Assets/Scripts/Campaign/Window.xaml` into its own resource scope; that template is not assumed to be available on the editor root.
- `UI/MissionCreatorText.cs` and `UI/Localization/` own Creator's labels.
- The project links the required files from `src/Shared/Game/`; source ownership remains shared, with no runtime common DLL or dependency on Advisor.

UI failures are logged under **AI Mission Creator**, independently of Advisor. Native window presentation and closing remain the game's responsibility. This is not a separate repository, and shared-source changes should be checked against both projects.

## Verification

Build both projects and run the existing Advisor suite before packaging. In the game, check opening, closing, repeated opening without duplicate windows, tab changes, editor exit/re-entry, and operation with AI Advisor disabled. These UI behaviors require native game verification; a successful build alone does not establish them.
