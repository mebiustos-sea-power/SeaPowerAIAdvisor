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
    private readonly AdvisorRuntime runtime;
    private readonly Action<string> warn;
    private readonly AdvisorOptionsStore options;
    private readonly AdvisorBackendRegistry backends;
    private readonly CampaignSituationReader reader = new();
    private object? session;
    private object? campaignUi;
    private AdvisorHudButton? launcher;
    private AdvisorWindow? advisor;
    private AdvisorErrorWindow? errorWindow;
    private Action? showError;

    internal CampaignIntegration(Assembly campaign, AdvisorRuntime runtime, Action<string> warn, AdvisorOptionsStore options, AdvisorBackendRegistry backends)
    {
        this.runtime = runtime; guard = runtime.Guard;
        this.warn = warn;
        this.options = options;
        this.backends = backends;
        var type = campaign.GetType("DynamicCampaignMod.Game.Session.CampaignSession", true)!;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        active = type.GetProperty("Active", flags)?.GetGetMethod(true) ?? throw new MissingMemberException(type.FullName, "Active");
        simulation = type.GetProperty("Sim", flags)?.GetGetMethod(true) ?? throw new MissingMemberException(type.FullName, "Sim");
        campaignScene = (int)(type.GetField("CampaignSceneIndex", flags)?.GetRawConstantValue()
            ?? throw new MissingMemberException(type.FullName, "CampaignSceneIndex"));
    }

    internal void Tick(object campaignUi)
    {
        Plugin.LocalApi?.Detach(this);
        if (!options.Current.CampaignEnabled ||
            (runtime.Failed ? SceneManager.GetActiveScene().buildIndex != campaignScene : !InCampaign()))
        {
            Reset();
            return;
        }
        var current = runtime.Failed ? session : simulation.Invoke(null, null)
            ?? throw new InvalidOperationException("An active campaign has no simulation.");
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
            void OpenAdvisor()
            {
                var currentAdvisor = GetAdvisor();
                currentAdvisor.Toggle();
                button.Control.IsChecked = currentAdvisor.IsOpen;
            }
            void ShowError()
            {
                var host = view.FindName("WindowHost") as WindowHost
                    ?? throw new InvalidOperationException("The campaign's own WindowHost is unavailable.");
                errorWindow ??= new AdvisorErrorWindow(view, host, runtime, AdvisorText.Load(GameLanguage.Read()),
                    value => button.Control.IsChecked = value, RestartAdvisor);
                errorWindow.Show();
            }
            void RestartAdvisor()
            {
                if (!runtime.Failed) return;
                errorWindow?.Close(); errorWindow = null;
                runtime.Restart(OpenAdvisor);
            }
            void Toggle()
            {
                if (!options.Current.CampaignEnabled) return;
                if (runtime.Failed)
                {
                    if (errorWindow?.IsOpen == true) errorWindow.Close();
                    else runtime.PresentFailure(ShowError);
                }
                else OpenAdvisor();
                button.Control.IsChecked = errorWindow?.IsOpen == true || advisor?.IsOpen == true;
            }
            showError = ShowError;
            button.Control.Click += (_, _) =>
            {
                if (runtime.Failed) runtime.Present(Toggle);
                else guard(Toggle);
            };
        }
        if (!runtime.Failed)
        {
            advisor?.Tick();
            if (options.Current.DeveloperApiEnabled) Plugin.LocalApi?.Attach(this, "dynamic_campaign", GetAdvisor);
        }
        if (showError != null) runtime.NotifyFailure(showError);
        errorWindow?.Tick();
        launcher.Control.IsChecked = errorWindow?.IsOpen == true || advisor?.IsOpen == true;
        launcher.SetStatus(advisor?.IsConsulting == true, advisor?.HasUnreadResult == true);
    }

    private AdvisorWindow GetAdvisor()
    {
        var view = CampaignView.Instance ?? throw new InvalidOperationException("The campaign view is unavailable.");
        var host = view.FindName("WindowHost") as WindowHost
            ?? throw new InvalidOperationException("The campaign's own WindowHost is unavailable.");
        return advisor ??= new AdvisorWindow(view, host, guard, new CampaignAdvisorSource(Capture), GameLanguage.Read, warn, options, backends);
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

    internal void Stop()
    {
        Plugin.LocalApi?.Detach(this);
        var previous = advisor; advisor = null;
        previous?.Stop();
    }

    internal void Reset()
    {
        try { Stop(); }
        finally
        {
            var previous = launcher; var report = errorWindow;
            advisor = null; errorWindow = null; showError = null; launcher = null; session = null; campaignUi = null;
            try { report?.Close(); }
            finally { previous?.Dispose(); }
        }
    }
}
