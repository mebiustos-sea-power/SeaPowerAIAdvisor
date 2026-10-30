using System;
using System.Diagnostics;
using Noesis;
using SeaPower;
using SeapowerUI;
using SeapowerUI.ViewModels;
using SeaPowerAIAdvisor.Shared;

namespace SeaPowerAIAdvisor.Crew;

// Independent mission lifetime: the tactical advisor's window and consultation runtime do not own Reports.
internal sealed class CrewIntegration
{
    private readonly Func<bool> enabled;
    private readonly Func<CrewSettingsStore, ICrewConnections> connect;
    private readonly Func<string, string> common;
    private readonly Action<Exception> log;
    private readonly string settingsPath;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private MainGameViewModel? model;
    private object? mission, player;
    private FrameworkElement? root;
    private MainGameView? view;
    private CrewReports? reports;
    private CrewBattleSource? source;
    private CrewSettingsStore? settings;
    private ICrewConnections? connections;
    private CrewWindow? window;
    private CrewPresentation? presentation;
    private HudButton? launcher;
    private double nextRefresh;
    private bool active, failed;
    internal CrewIntegration(string settingsPath, Func<bool> enabled, Func<CrewSettingsStore, ICrewConnections> connect, Func<string, string> common, Action<Exception> log)
    { this.settingsPath = settingsPath; this.enabled = enabled; this.connect = connect; this.common = common; this.log = log; }
    internal void Tick(MainGameViewModel current) => Guard(() => Update(current));
    private void Update(MainGameViewModel current)
    {
        if (current.RootControl == null || !SceneCreator.InstanceExists(false) || !SceneCreator.Instance.IsLoadingDone || Globals._playerTaskforce == null)
        { Reset(); return; }
        if (!ReferenceEquals(model, current) || !ReferenceEquals(root, current.RootControl) || !ReferenceEquals(mission, SceneCreator.Instance) || !ReferenceEquals(player, Globals._playerTaskforce))
        {
            Reset(); model = current; root = current.RootControl; mission = SceneCreator.Instance; player = Globals._playerTaskforce;
        }
        if (!enabled()) { if (active || window != null || launcher != null || presentation != null) Suspend(); failed = false; return; }
        if (failed)
        {
            window?.Tick();
            if (launcher != null) { launcher.Control.Visibility = current.IsHudVisible ? Visibility.Visible : Visibility.Collapsed; launcher.Control.IsChecked = window?.IsOpen == true; }
            return;
        }
        view ??= GameUi.FindVisual<MainGameView>(current.RootControl);
        if (view == null || view.ActualWidth <= 0 || view.ActualHeight <= 0) return;
        if (!active)
        {
            settings ??= new CrewSettingsStore(settingsPath); connections ??= connect(settings);
            source ??= new CrewBattleSource(current); reports ??= new CrewReports(log);
            reports.Refresh(source.Targets()); nextRefresh = clock.Elapsed.TotalSeconds + 1;
            var grid = view.FindName("DisplayAreaGrid") as Grid ?? throw new InvalidOperationException("The battle HUD grid is unavailable.");
            var reference = view.FindName("UnitStatusWindow") as ToggleButton ?? throw new InvalidOperationException("The battle HUD toggle is unavailable.");
            launcher = new HudButton(grid, reference, "RPT", "AI Crew - Reports", 11);
            presentation = new CrewPresentation(GameUi.FindVisual<Grid>(view) ?? throw new InvalidOperationException("The battle content layout is unavailable."));
            launcher.Control.Click += (_, _) => Guard(() =>
            {
                var host = GameUi.FindWindowHost(current.RootControl) ?? throw new InvalidOperationException("The battle window host is unavailable.");
                window ??= new CrewWindow(view, host, reports, settings, connections, common, new CrewText(GameLanguage.Read()), presentation.Portrait,
                    () => clock.Elapsed.TotalSeconds, Guard, log);
                window.Toggle(); if (failed) window.Stop(); launcher.Control.IsChecked = window.IsOpen;
            });
            active = true;
        }
        var now = clock.Elapsed.TotalSeconds;
        if (now >= nextRefresh) { reports!.Refresh(source!.Targets()); nextRefresh = now + 1; }
        reports!.Tick(now, settings!.Current, GameLanguage.Read(), source!.Capture, connections!.GenerateAsync);
        window?.Tick(); presentation!.Tick(reports, now, settings.Current.DisplaySeconds, settings.Current.NotificationPosition, current.IsHudVisible);
        launcher!.Control.Visibility = current.IsHudVisible ? Visibility.Visible : Visibility.Collapsed;
        launcher.Control.IsChecked = window?.IsOpen == true;
        launcher.Control.ToolTip = reports.Error == null ? "AI Crew - Reports" : "AI Crew - Reports / " + reports.Error;
    }
    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception error)
        {
            log(error); failed = true;
            Cleanup(() => reports?.Suspend(), () => presentation?.Clear(), () => window?.Stop());
            if (launcher != null) launcher.Control.ToolTip = "AI Crew - Reports / " + error.GetType().Name;
        }
    }
    private void Cleanup(params Action[] actions)
    {
        foreach (var action in actions) try { action(); } catch (Exception error) { log(error); }
    }
    private void Suspend()
    {
        active = false;
        var oldWindow = window; window = null;
        var oldPresentation = presentation; presentation = null;
        var oldLauncher = launcher; launcher = null;
        Cleanup(() => reports?.Suspend(), () => oldWindow?.Dispose(), () => oldPresentation?.Dispose(), () => oldLauncher?.Dispose());
    }
    internal void Destroy(MainGameViewModel current) { if (ReferenceEquals(model, current)) Guard(Reset); }
    private void Reset()
    {
        Suspend(); var oldReports = reports; reports = null; Cleanup(() => oldReports?.Dispose()); source = null; connections = null; settings = null;
        model = null; root = null; view = null; mission = null; player = null; failed = false; nextRefresh = 0;
    }
}
