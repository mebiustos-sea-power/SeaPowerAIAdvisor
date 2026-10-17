using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AnchorChain;
using BepInEx.Logging;
using HarmonyLib;
using SeapowerUI.ViewModels;

namespace SeaPowerAIAdvisor;

[ACPlugin(ModId, "Sea Power AI Advisor Prototype", "0.12.0", new string[0], new string[0])]
public sealed class Plugin : IAnchorChainMod
{
    public const string ModId = "mebiustos.seapower.aiadvisor";
    private static readonly ManualLogSource Log = Logger.CreateLogSource("Sea Power AI Advisor");
    private static Harmony? harmony;
    private static CampaignIntegration? campaign;
    private static BattleIntegration? battle;
    private static AdvisorOptionsStore? options;
    private static Type? campaignFiles;
    private static bool campaignInstallAttempted;
    private static bool installed, campaignFailed, battleFailed;

    public void TriggerEntryPoint()
    {
        if (installed) return;
        try
        {
            var game = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Seapower-Scripts");
            var files = game.GetType("SeaPower.FileManager", true)!;
            if (!ModActivation.IsEnabled(files, Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!))
            {
                Log.LogInfo("Disabled in the mod manager; advisor was not installed.");
                return;
            }
            options = new AdvisorOptionsStore(AdvisorOptionsStore.PathIn(UnityEngine.Application.persistentDataPath));
            // A broken prompt package must remain visible without disabling settings or diagnostics.
            try { _ = AdvisorPrompts.Current; }
            catch (AdvisorPromptException error) { Log.LogError("System prompts unavailable until restart: " + error.Message); }
            campaignFiles = files;
            AdvisorDependencies.LoadMarkdown(typeof(Plugin).Assembly);
            harmony = new Harmony(ModId);
            battle = new BattleIntegration(GuardBattle, message => Log.LogWarning(message), options);
            harmony.Patch(Required(typeof(MainGameViewModel), "OnLateUpdate"), postfix: new HarmonyMethod(typeof(Plugin), nameof(BattleFrame)));
            harmony.Patch(Required(typeof(MainGameViewModel), "OnDestroy"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BattleDestroyed)));
            installed = true;
            Log.LogInfo("Advisor 0.12.0 ready for tactical battles. DCM is optional. Open AI in the bottom-right battle toolbar.");
            TryInstallCampaign();
        }
        catch (Exception error)
        {
            Log.LogError("Advisor initialization failed: " + error);
            battleFailed = true;
            try { battle?.Reset(); } catch (Exception cleanup) { Log.LogError(cleanup); }
        }
    }

    private static void TryInstallCampaign()
    {
        if (options == null || !options.Current.CampaignEnabled || campaignInstallAttempted) return;
        campaignInstallAttempted = true;
        GuardCampaign(() => InstallCampaign(campaignFiles ?? throw new InvalidOperationException("The mod activation context is missing.")));
    }

    private static void InstallCampaign(Type files)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "DynamicCampaignMod");
        if (assembly == null)
        {
            Log.LogInfo("DCM is not installed; tactical advisor is available independently.");
            return;
        }
        if (!ModActivation.IsEnabled(files, Path.GetDirectoryName(assembly.Location)!))
        {
            Log.LogInfo("DCM is disabled; tactical advisor is available independently.");
            return;
        }
        campaign = new CampaignIntegration(assembly, GuardCampaign, message => Log.LogWarning(message), options!);
        var ui = assembly.GetType("DynamicCampaignMod.UI.CampaignUI", true)!;
        harmony!.Patch(Required(ui, "Frame"), postfix: new HarmonyMethod(typeof(Plugin), nameof(CampaignFrame)));
        harmony.Patch(Required(ui, "Detach"), prefix: new HarmonyMethod(typeof(Plugin), nameof(CampaignDetach)));
        var session = assembly.GetType("DynamicCampaignMod.Game.Session.CampaignSession", true)!;
        harmony.Patch(Required(session, "OnSceneUnloaded"), prefix: new HarmonyMethod(typeof(Plugin), nameof(CampaignSceneUnloaded)));
        Log.LogInfo("Optional DCM campaign advisor installed.");
    }

    private static void BattleFrame(MainGameViewModel __instance)
    {
        GuardBattle(() => battle!.Tick(__instance));
        TryInstallCampaign();
    }
    private static void BattleDestroyed(MainGameViewModel __instance) => GuardBattle(() => battle?.Destroy(__instance));
    private static void CampaignFrame(object __instance) => GuardCampaign(() => campaign!.Tick(__instance));
    private static void CampaignDetach() => GuardCampaign(() => campaign?.Reset());
    private static void CampaignSceneUnloaded(UnityEngine.SceneManagement.Scene scene) => GuardCampaign(() => campaign?.SceneUnloaded(scene));

    private static MethodInfo Required(Type type, string name) =>
        AccessTools.Method(type, name) ?? throw new MissingMethodException(type.FullName, name);

    private static void GuardBattle(Action action)
    {
        if (battleFailed) return;
        try { action(); }
        catch (Exception error)
        {
            battleFailed = true;
            Log.LogError("Battle advisor stopped until restart: " + error);
            try { battle?.Reset(); } catch (Exception cleanup) { Log.LogError(cleanup); }
        }
    }

    private static void GuardCampaign(Action action)
    {
        if (campaignFailed) return;
        try { action(); }
        catch (Exception error)
        {
            campaignFailed = true;
            Log.LogError("DCM advisor stopped until restart: " + error);
            try { campaign?.Reset(); } catch (Exception cleanup) { Log.LogError(cleanup); }
        }
    }
}
