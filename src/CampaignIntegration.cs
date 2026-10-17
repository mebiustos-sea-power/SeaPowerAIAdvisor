using System;
using System.Reflection;
using Noesis;
using SeapowerUI;
using UnityEngine.SceneManagement;

namespace SeaPowerAIAdvisor;

internal sealed class CampaignIntegration
{
    private readonly MethodInfo active;
    private readonly MethodInfo simulation;
    private readonly int campaignScene;
    private readonly Action<Action> guard;
    private readonly Action<string> warn;
    private readonly AdvisorOptionsStore options;
    private readonly CampaignSituationReader reader = new();
    private object? session;
    private object? campaignUi;
    private AdvisorHudButton? launcher;
    private AdvisorWindow? advisor;

    internal CampaignIntegration(Assembly campaign, Action<Action> guard, Action<string> warn, AdvisorOptionsStore options)
    {
        this.guard = guard;
        this.warn = warn;
        this.options = options;
        var type = campaign.GetType("DynamicCampaignMod.Game.Session.CampaignSession", true)!;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        active = type.GetProperty("Active", flags)?.GetGetMethod(true) ?? throw new MissingMemberException(type.FullName, "Active");
        simulation = type.GetProperty("Sim", flags)?.GetGetMethod(true) ?? throw new MissingMemberException(type.FullName, "Sim");
        campaignScene = (int)(type.GetField("CampaignSceneIndex", flags)?.GetRawConstantValue()
            ?? throw new MissingMemberException(type.FullName, "CampaignSceneIndex"));
    }

    internal void Tick(object campaignUi)
    {
        if (!options.Current.CampaignEnabled || !InCampaign())
        {
            Reset();
            return;
        }
        var current = simulation.Invoke(null, null) ?? throw new InvalidOperationException("An active campaign has no simulation.");
        var view = CampaignView.Instance;
        if (view == null || view.ActualWidth <= 0 || view.ActualHeight <= 0) return;
        if (!ReferenceEquals(session, current))
        {
            Reset();
            session = current;
        }
        this.campaignUi = campaignUi;
        if (launcher == null)
        {
            var grid = view.Content as Grid
                ?? throw new InvalidOperationException("The campaign HUD grid is unavailable.");
            var reference = view.FindName("EventLogWindow") as ToggleButton
                ?? throw new InvalidOperationException("The campaign HUD toggle button is unavailable.");
            // Wait until the campaign's size-to-cell toggle has been arranged.
            if (reference.ActualWidth <= 0 || reference.ActualHeight <= 0) return;
            var button = new AdvisorHudButton(grid, reference, AdvisorText.Load(GameLanguage.Read())["launcher.tooltip"]);
            launcher = button;
            button.Control.Click += (_, _) => guard(() =>
            {
                if (!options.Current.CampaignEnabled) return;
                var host = view.FindName("WindowHost") as WindowHost
                    ?? throw new InvalidOperationException("The campaign's own WindowHost is unavailable.");
                advisor ??= new AdvisorWindow(view, host, guard, new CampaignAdvisorSource(Capture), GameLanguage.Read, warn, options);
                advisor.Toggle();
                button.Control.IsChecked = advisor.IsOpen;
            });
        }
        advisor?.Tick();
        launcher.Control.IsChecked = advisor?.IsOpen == true;
    }

    private bool InCampaign() => (bool)active.Invoke(null, null)! && SceneManager.GetActiveScene().buildIndex == campaignScene;

    private SituationSnapshot Capture()
    {
        if (!options.Current.CampaignEnabled || !InCampaign() || session == null || !ReferenceEquals(session, simulation.Invoke(null, null)))
            throw new SituationUnavailableException("campaign_unavailable");
        var names = new CampaignDisplayNames(campaignUi
            ?? throw new InvalidOperationException("The campaign display context is missing."), session);
        return reader.Capture(session, names.ShipName);
    }

    internal void SceneUnloaded(Scene scene)
    {
        if (scene.buildIndex == campaignScene) Reset();
    }

    internal void Reset()
    {
        try { advisor?.Close(); }
        finally
        {
            var previous = launcher;
            advisor = null;
            launcher = null;
            session = null;
            campaignUi = null;
            previous?.Dispose();
        }
    }
}
