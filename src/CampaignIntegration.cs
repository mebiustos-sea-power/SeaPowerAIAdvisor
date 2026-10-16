using System;
using System.Linq;
using System.Reflection;
using Noesis;
using SeapowerUI;
using UnityEngine.SceneManagement;

namespace SeaPowerAIAdvisor;

internal sealed class CampaignIntegration
{
    private readonly MethodInfo active;
    private readonly MethodInfo simulation;
    private readonly FieldInfo strategyView;
    private readonly int campaignScene;
    private readonly Action<Action> guard;
    private readonly Action<string> warn;
    private readonly AdvisorOptionsStore options;
    private readonly CampaignSituationReader reader = new();
    private object? session;
    private object? campaignUi;
    private StackPanel? toolbar;
    private ToggleButton? launcher;
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
        var ui = campaign.GetType("DynamicCampaignMod.UI.CampaignUI", true)!;
        strategyView = ui.GetField("_strategyView", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(ui.FullName, "_strategyView");
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
            // The original toolbar can still be awaiting attachment during initialization.
            var strategy = strategyView.GetValue(campaignUi);
            if (strategy == null) return;
            var root = strategy as Grid ?? throw new InvalidOperationException("The campaign strategy view is no longer a Grid.");
            toolbar = root.Children.Cast<UIElement>().OfType<Border>()
                .Where(b => Grid.GetColumn(b) == 0).Select(b => b.Child).OfType<StackPanel>().SingleOrDefault()
                ?? throw new InvalidOperationException("The campaign's strategy toolbar is unavailable.");
            launcher = new ToggleButton
            {
                Content = "AI",
                ToolTip = AdvisorText.Load(GameLanguage.Read())["launcher.tooltip"],
                FontSize = 15,
                Style = root.TryFindResource("StrategyButton") as Style
                    ?? throw new InvalidOperationException("The campaign's StrategyButton style is unavailable.")
            };
            launcher.Click += (_, _) => guard(() =>
            {
                if (!options.Current.CampaignEnabled) return;
                var host = view.FindName("WindowHost") as WindowHost
                    ?? throw new InvalidOperationException("The campaign's own WindowHost is unavailable.");
                advisor ??= new AdvisorWindow(view, host, guard, new CampaignAdvisorSource(Capture), GameLanguage.Read, warn, options);
                advisor.Toggle();
                launcher.IsChecked = advisor.IsOpen;
            });
            // Participate in the existing layout instead of covering the clock controls.
            toolbar.Children.Insert(Math.Min(1, toolbar.Children.Count), launcher);
        }
        advisor?.Tick();
        launcher.IsChecked = advisor?.IsOpen == true;
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
            try
            {
                if (toolbar != null && launcher != null) toolbar.Children.Remove(launcher);
            }
            finally
            {
                advisor = null;
                launcher = null;
                toolbar = null;
                session = null;
                campaignUi = null;
            }
        }
    }
}
