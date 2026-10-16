using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AnchorChain;
using BepInEx.Logging;
using HarmonyLib;

namespace SeaPowerAIAdvisor;

[ACPlugin(ModId, "Sea Power AI Advisor Prototype", "0.9.1", new[] { "dynamiccampaignmod" }, new string[0])]
[ACDependency("dynamiccampaignmod", null, null)]
public sealed class Plugin : IAnchorChainMod
{
    public const string ModId = "mebiustos.seapower.aiadvisor";
    private static readonly ManualLogSource Log = Logger.CreateLogSource("Sea Power AI Advisor");
    private static Harmony? harmony;
    private static Assembly? campaign;
    private static CampaignIntegration? integration;
    private static bool scheduled;
    private static bool installed;
    private static bool failed;

    public void TriggerEntryPoint()
    {
        if (scheduled || installed || failed) return;
        Guard(() =>
        {
            var game = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Seapower-Scripts");
            var files = game.GetType("SeaPower.FileManager", true)!;
            var package = Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!;
            if (!ModActivation.IsEnabled(files, package))
            {
                Log.LogInfo("Disabled in the mod manager; UI prototype was not installed.");
                return;
            }
            campaign = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "DynamicCampaignMod")
                ?? throw new InvalidOperationException("DynamicCampaignMod.dll is not loaded.");
            if (!ModActivation.IsEnabled(files, Path.GetDirectoryName(campaign.Location)!))
                throw new InvalidOperationException("Dynamic Campaign Mod is disabled.");
            var deferred = campaign.GetType("DynamicCampaignMod.DeferredPatcher", true)!;
            harmony = new Harmony(ModId);
            harmony.Patch(Required(deferred, "Patch"), postfix: new HarmonyMethod(typeof(Plugin), nameof(Install)));
            scheduled = true;
            Log.LogInfo("Advisor scheduled after campaign initialization.");
        });
    }

    private static void Install() => Guard(() =>
    {
        if (installed) return;
        integration = new CampaignIntegration(campaign!, Guard, message => Log.LogWarning(message));
        var ui = campaign!.GetType("DynamicCampaignMod.UI.CampaignUI", true)!;
        harmony!.Patch(Required(ui, "Frame"), postfix: new HarmonyMethod(typeof(Plugin), nameof(Frame)));
        harmony.Patch(Required(ui, "Detach"), prefix: new HarmonyMethod(typeof(Plugin), nameof(Detach)));
        var session = campaign.GetType("DynamicCampaignMod.Game.Session.CampaignSession", true)!;
        harmony.Patch(Required(session, "OnSceneUnloaded"), prefix: new HarmonyMethod(typeof(Plugin), nameof(SceneUnloaded)));
        installed = true;
        Log.LogInfo("Gemini advisor 0.9.1 ready. Use AI in the strategy toolbar. Player-known on-demand knowledge queries with victory objectives; reply language follows the game; sends only on explicit consultation.");
    });

    private static void Frame(object __instance) => Guard(() => integration!.Tick(__instance));
    private static void Detach() => Guard(() => integration?.Reset());
    private static void SceneUnloaded(UnityEngine.SceneManagement.Scene scene) => Guard(() => integration?.SceneUnloaded(scene));

    private static MethodInfo Required(Type type, string name) =>
        AccessTools.Method(type, name) ?? throw new MissingMethodException(type.FullName, name);

    private static void Guard(Action action)
    {
        if (failed) return;
        try { action(); }
        catch (Exception error)
        {
            failed = true;
            Log.LogError("Advisor prototype stopped until restart: " + error);
            try { integration?.Reset(); }
            catch (Exception cleanupError) { Log.LogError("Advisor UI cleanup failed: " + cleanupError); }
        }
    }
}
