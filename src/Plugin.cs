using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AnchorChain;
using BepInEx.Logging;
using HarmonyLib;
using SeapowerUI.ViewModels;

namespace SeaPowerAIAdvisor;

[ACPlugin(ModId, "Sea Power AI Advisor Prototype", "0.18.0", new string[0], new string[0])]
public sealed class Plugin : IAnchorChainMod
{
    public const string ModId = "mebiustos.seapower.aiadvisor";
    private static readonly ManualLogSource Log = Logger.CreateLogSource("Sea Power AI Advisor");
    private static Harmony? harmony;
    private static CampaignIntegration? campaign;
    private static BattleIntegration? battle;
    private static AdvisorOptionsStore? options;
    private static AdvisorBackendRegistry? backends;
    internal static AdvisorLocalApi? LocalApi { get; private set; }
    internal static AdvisorBackendRegistry Backends => backends
        ?? throw new InvalidOperationException("The adviser backend registry has not been initialized.");
    private static Type? campaignFiles;
    private static bool campaignInstallAttempted;
    private static bool installed;
    private static readonly AdvisorRuntime BattleRuntime = new("tactical_battle", message => Log.LogError(message), () => battle?.Stop());
    private static readonly AdvisorRuntime CampaignRuntime = new("dynamic_campaign", message => Log.LogError(message), () => campaign?.Stop());

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
            AdvisorErrorHistory.StartRun(message => Log.LogWarning(message));
            options = new AdvisorOptionsStore(AdvisorOptionsStore.PathIn(UnityEngine.Application.persistentDataPath));
            var settingsPath = AdvisorSettings.MigrateFileName(UnityEngine.Application.persistentDataPath);
            backends = AdvisorServices.CreateRegistry(settingsPath);
            // A broken prompt package must remain visible without disabling settings or diagnostics.
            try { _ = AdvisorPrompts.Current; }
            catch (AdvisorPromptException error) { Log.LogError("System prompts unavailable until restart: " + error.Message); }
            campaignFiles = files;
            EmbeddedDependencies.LoadMarkdown(typeof(Plugin).Assembly);
            harmony = new Harmony(ModId);
            battle = new BattleIntegration(BattleRuntime, message => Log.LogWarning(message), options, Backends);
            harmony.Patch(Required(typeof(MainGameViewModel), "OnLateUpdate"), postfix: new HarmonyMethod(typeof(Plugin), nameof(BattleFrame)));
            harmony.Patch(Required(typeof(MainGameViewModel), "OnDestroy"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BattleDestroyed)));
            SeaPowerAIAdvisor.Api.AdvisorApi.Attach(extension =>
            {
                var owner = extension.GetType().Assembly;
                if (!ModActivation.IsEnabled(files, Path.GetDirectoryName(owner.Location)!)) return false;
                AdvisorServiceCatalog.BuiltIn.Register(extension);
                Log.LogInfo("Registered AI extension: " + extension.Id);
                return true;
            }, extension =>
            {
                var owner = extension.GetType().Assembly;
                if (!ModActivation.IsEnabled(files, Path.GetDirectoryName(owner.Location)!)) return false;
                var id = AdvisorOperationCatalog.BuiltIn.Register(extension);
                Log.LogInfo("Registered operation extension: " + id);
                return true;
            });
            LocalApi = new AdvisorLocalApi(() => AdvisorPromptOverrides.Session, error => Log.LogError("Developer local API failed: " + error),
                () => options.Current.LocalConnections.Describe(AdvisorSettings.Edit(settingsPath)), (id, allowFallback, model, delivery, prompt) =>
                {
                    var saved = AdvisorSettings.Edit(settingsPath);
                    options.Current.LocalConnections.Select(saved, id, allowFallback, model, prompt);
                    return AdvisorServices.Prepare(saved, allowFallback: allowFallback, situationDelivery: delivery);
                });
            var driverObject = new UnityEngine.GameObject("SeaPowerAIAdvisor.DeveloperLocalApi");
            UnityEngine.Object.DontDestroyOnLoad(driverObject);
            driverObject.AddComponent<AdvisorLocalApiDriver>().Initialize(options, LocalApi, message => Log.LogWarning(message));
            installed = true;
            Log.LogInfo("Advisor 0.18.0 ready for tactical battles. DCM is optional. Open AI in the bottom-right battle toolbar.");
            TryInstallCampaign();
        }
        catch (Exception error)
        {
            SeaPowerAIAdvisor.Api.AdvisorApi.Detach();
            BattleRuntime.Fail(error);
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
        campaign = new CampaignIntegration(assembly, CampaignRuntime, message => Log.LogWarning(message), options!, Backends);
        var ui = assembly.GetType("DynamicCampaignMod.UI.CampaignUI", true)!;
        harmony!.Patch(Required(ui, "Frame"), postfix: new HarmonyMethod(typeof(Plugin), nameof(CampaignFrame)));
        harmony.Patch(Required(ui, "Detach"), prefix: new HarmonyMethod(typeof(Plugin), nameof(CampaignDetach)));
        var session = assembly.GetType("DynamicCampaignMod.Game.Session.CampaignSession", true)!;
        harmony.Patch(Required(session, "OnSceneUnloaded"), prefix: new HarmonyMethod(typeof(Plugin), nameof(CampaignSceneUnloaded)));
        Log.LogInfo("Optional DCM campaign advisor installed.");
    }

    private static void BattleFrame(MainGameViewModel __instance)
    {
        BattleRuntime.Tick(() => battle?.Tick(__instance));
        TryInstallCampaign();
    }
    private static void BattleDestroyed(MainGameViewModel __instance) => BattleRuntime.Cleanup(() => battle?.Destroy(__instance));
    private static void CampaignFrame(object __instance) => CampaignRuntime.Tick(() => campaign?.Tick(__instance));
    private static void CampaignDetach() => CampaignRuntime.Cleanup(() => campaign?.Reset());
    private static void CampaignSceneUnloaded(UnityEngine.SceneManagement.Scene scene) => CampaignRuntime.Cleanup(() => campaign?.SceneUnloaded(scene));

    private static MethodInfo Required(Type type, string name) =>
        AccessTools.Method(type, name) ?? throw new MissingMethodException(type.FullName, name);

    private static void GuardCampaign(Action action) => CampaignRuntime.Guard(action);
}
