using System;
using System.IO;
using System.Linq;
using AnchorChain;
using BepInEx.Logging;
using HarmonyLib;
using SeapowerUI;
using SeaPowerAIAdvisor.Shared;
using SeaPowerAIMissionCreator.Editor;
using SeaPowerAIMissionCreator.Configuration;
using SeaPowerAIMissionCreator.Connections;
using SeaPowerAIMissionCreator.Development;
using UnityEngine;

namespace SeaPowerAIMissionCreator;

[ACPlugin(ModId, "AI Mission Creator Prototype", "0.1.0", new string[0], new string[0])]
public sealed class Plugin : IAnchorChainMod
{
    public const string ModId = "mebiustos.seapower.aimissioncreator";
    private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("AI Mission Creator");
    private static bool installed;
    private static MissionCreatorConnections? connections;
    private static MissionCreatorLocalApiDriver? driver;

    public void TriggerEntryPoint()
    {
        if (installed) return;
        try
        {
            var game = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Seapower-Scripts");
            var files = game.GetType("SeaPower.FileManager", true)!;
            if (!ModActivation.IsEnabled(files, Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!)) return;
            connections = new MissionCreatorConnections(new MissionCreatorSettingsStore(Path.Combine(Application.persistentDataPath, "SeaPowerAIMissionCreator")),
                error => Log.LogError("Mission Creator connection failed: " + error));
            var runtime = new GameObject("SeaPowerAIMissionCreator.Runtime"); UnityEngine.Object.DontDestroyOnLoad(runtime);
            driver = runtime.AddComponent<MissionCreatorLocalApiDriver>(); driver.Initialize(connections, message => Log.LogWarning(message));
            var constructor = AccessTools.Constructor(typeof(MissionEditorView), Type.EmptyTypes)
                ?? throw new MissingMethodException(typeof(MissionEditorView).FullName, ".ctor");
            new Harmony(ModId).Patch(constructor, postfix: new HarmonyMethod(typeof(Plugin), nameof(EditorCreated)));
            installed = true;
            // Attach an editor that the menu created before MOD initialization as well.
            if (MissionEditorView.Instance != null) EditorCreated(MissionEditorView.Instance);
            Log.LogInfo("Mission Creator ready. Open AI Mission Creator beside the mission editor title.");
        }
        catch (Exception error)
        {
            if (driver != null) UnityEngine.Object.Destroy(driver.gameObject);
            connections?.Dispose(); connections = null; driver = null;
            Log.LogError("Mission Creator initialization failed: " + error);
        }
    }

    private static void EditorCreated(MissionEditorView __instance)
    {
        try { _ = new MissionCreatorEditor(__instance, connections ?? throw new InvalidOperationException("Creator connections are unavailable."), driver ?? throw new InvalidOperationException("Creator runtime is unavailable."), error => Log.LogError("Mission Creator UI failed: " + error)); }
        catch (Exception error) { Log.LogError("Mission Creator editor integration failed: " + error); }
    }
}
