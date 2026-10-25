# External command example

This is an independent Anchor Chain MOD that registers `example.seapower.sonobuoy_drop` with AI Advisor. It adds **Example: sonobuoy drop reservation** alongside the built-in command. Adopting its proposal appends real movement/drop reservations; loading the MOD does not issue orders.

The example uses only `SeaPowerAIAdvisor.Api.dll`, Anchor Chain and the game's Newtonsoft.Json library. It references neither the host implementation DLL nor host sources and has no project references. The SDK DLL must come from a command-SDK build: the published v0.17.1 API does not contain these contracts.

## Build without rebuilding AI Advisor

Use a .NET SDK supporting C# 12 and `netstandard2.1`. Keep an already-built copy of the host and API DLLs fixed. From this directory, in PowerShell:

```powershell
dotnet build .\SeaPowerAIAdvisor.ExampleCommand.csproj -c Release `
  -p:AdvisorApiPath="C:\SDK\SeaPowerAIAdvisor.Api.dll" `
  -p:AnchorChainDir="C:\Steam\steamapps\workshop\content\1286220\3380210757" `
  -p:SeaPowerDir="C:\Steam\steamapps\common\Sea Power"
```

All paths are examples; supply your actual paths. These properties work when this entire directory is copied out of the repository. There is no automatic build or fallback to the host source project.

## Install

Create a separate `Sea Power_Data/StreamingAssets/SeaPowerAIAdvisor.ExampleCommand/` MOD folder and copy only these files from `bin/Release/netstandard2.1/`:

```text
SeaPowerAIAdvisor.ExampleCommand.dll
_info.ini
```

Enable the example, AI Advisor and Anchor Chain in the game's MOD manager, and completely restart Sea Power. Do **not** copy `SeaPowerAIAdvisor.Api.dll` into the example folder: the host supplies it. Localization files are embedded in the example DLL. The sample is not part of AI Advisor's normal runtime package.

`Plugin.cs` declares a dependency on AI Advisor and an entry-point ordering constraint. Anchor Chain discovers public types before applying that order, so the public `Plugin` type does not expose SDK types in its metadata. SDK-dependent implementations stay internal; registration is deferred to `CommandRegistration.Register()`. A disabled host leaves `AdvisorApi.IsAvailable` false; the sample then does not register. When this MOD is disabled, `RegisterOperation` returns false without adding a command. Neither disabled state is an error. Other registration failures stay visible.

## Command behavior

`SonobuoyCommand.cs` implements registration metadata, candidates, side-effect-free validation/review, optional preview and execution. It also demonstrates the optional `IAdvisorExtensionVersion`: its `Version` identifies this command itself and appears in Settings > Extensions > Commands. Omit the interface to leave the version undisplayed. SDK compatibility still uses `ApiVersion`; see the [SDK stability notice and display-version contract](../../docs/public-api.md). English and Japanese text belong to this MOD; the host provides language fallback. Each operation takes a target reference and three arguments:

```json
{
  "operation_id": "example.seapower.sonobuoy_drop",
  "target_ref": "unit:1029",
  "arguments": {
    "latitude": 26.43569,
    "longitude": 127.36818,
    "ammunition": "usn_ssq-41"
  }
}
```

Use actual current candidate IDs, ammunition and coordinates, not the example values. The host's AI submission envelope groups these operations into proposals. The example uses the default plan policy: one plan with single selection. A plan may contain multiple drops and targets; one operation schedules one drop. It requires approval and rechecks the current target and inventory before executing. Combined inventory is validated per target/ammunition across the selected operations. The game support service appends to existing reservations and retains the player's current depth setting. This example expects canonical ammunition IDs and intentionally omits optional argument normalization.

Preview returns points and lines to the host, which owns their display and cleanup. When movement-plan information is allowed, the line starts at the existing reservation endpoint or current position. Preview does not recenter the map or draw directly. Executing the command uses the same public game support as the built-in command. It does not monitor arrival, release or deployment completion.

The session implements `IAdvisorOperationHandler` for candidates/disposal and `IAdvisorOperationActions` for validation, localized explanation and the post-approval action. This sample is a noncomposing command: its plan uses an empty `reservation_updates` array. A command that shares a target queue with movement or other commands instead implements the reservation contracts in [the public API guide](../../docs/public-api.md).

The host owns proposal lifetime, approval, request IDs and duplicate-request protection. The external handler does not implement a second approval workflow or HTTP listener.

## Test

Enable **Commands**, own-unit information and weapon information in AI Advisor. In a paused battle with an eligible aircraft, inspect the example in Settings > Commands, then ask explicitly for the **Example: sonobuoy drop reservation** command so the AI can distinguish it from the built-in command. Review and adopt the proposal before resuming the game. Check the existing queue remains intact and the current depth setting is unchanged.

The developer API can also list candidates and use `operations submit --request-file FILE` with one plan, an empty `reservation_updates` array, and operations using this operation ID. That direct test endpoint bypasses the AI approval UI; it does not demonstrate that the approval screen was operated. Use a disposable copy of a test save and do not overwrite the master.

Offline behavior checks use a fake public game context and the **already-built** sample DLL. With a .NET 10 SDK:

```powershell
dotnet run --project .\tests\ExternalCommand.Tests.csproj -c Release `
  -p:AdvisorApiPath="C:\SDK\SeaPowerAIAdvisor.Api.dll" `
  -p:SampleDllPath="C:\Samples\ExternalCommand\bin\Release\netstandard2.1\SeaPowerAIAdvisor.ExampleCommand.dll" `
  -p:SeaPowerDir="C:\Steam\steamapps\common\Sea Power"
```

The checks cover disabled-host/MOD registration, candidate availability, disclosure permissions, invalid input, combined inventory, review/preview without writes, exact execution and revalidation after live changes. They neither rebuild the host nor issue game orders. Hash the installed host/API DLLs before and after the real-game test to verify the addition is independent. Game verification status is recorded in the SDK design document; do not treat offline checks as proof of the full in-game approval flow.
