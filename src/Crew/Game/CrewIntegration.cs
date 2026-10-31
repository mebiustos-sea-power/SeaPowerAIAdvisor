using System;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json.Linq;
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
    private readonly Action<bool> saveEnabled;
    private readonly Action<string> checkLocalConnection;
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
    private CrewLocalApi? local;
    private CrewBattleSource? source;
    private CrewSettingsStore? settings;
    private ICrewConnections? connections;
    private CrewWindow? window;
    private CrewPresentation? presentation;
    private HudButton? launcher;
    private double nextRefresh;
    private bool active, failed;
    internal CrewIntegration(string settingsPath, Func<bool> enabled, Action<bool> saveEnabled, Action<string> checkLocalConnection, Func<CrewSettingsStore, ICrewConnections> connect, Func<string, string> common, Action<Exception> log)
    { this.settingsPath = settingsPath; this.enabled = enabled; this.saveEnabled = saveEnabled; this.checkLocalConnection = checkLocalConnection; this.connect = connect; this.common = common; this.log = log; }
    internal void Tick(MainGameViewModel current, ToggleButton? hudAnchor) => Guard(() => Update(current, hudAnchor));
    private void Update(MainGameViewModel current, ToggleButton? hudAnchor)
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
            local ??= new CrewLocalApi(reports, () => clock.Elapsed.TotalSeconds, () =>
            {
                connections.ValidateSelection(settings.Current.ConnectionId);
                checkLocalConnection(settings.Current.ConnectionId!);
            });
            reports.Refresh(source.Targets()); nextRefresh = clock.Elapsed.TotalSeconds + 1;
            var grid = view.FindName("DisplayAreaGrid") as Grid ?? throw new InvalidOperationException("The battle HUD grid is unavailable.");
            var reference = view.FindName("UnitStatusWindow") as ToggleButton ?? throw new InvalidOperationException("The battle HUD toggle is unavailable.");
            launcher = new HudButton(grid, reference, "RPT", "AI Crew - Reports", 11, before: hudAnchor);
            presentation = new CrewPresentation(GameUi.FindVisual<Grid>(view) ?? throw new InvalidOperationException("The battle content layout is unavailable."));
            launcher.Control.Click += (_, _) => Guard(ToggleWindow);
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
    private void ToggleWindow()
    {
        var current = model ?? throw new InvalidOperationException("The battle model is unavailable.");
        var host = GameUi.FindWindowHost(current.RootControl) ?? throw new InvalidOperationException("The battle window host is unavailable.");
        window ??= new CrewWindow(view!, host, reports!, settings!, connections!, common, new CrewText(GameLanguage.Read()), presentation!.Portrait,
            () => clock.Elapsed.TotalSeconds, Guard, log);
        window.Toggle(); if (failed) window.Stop(); launcher!.Control.IsChecked = window.IsOpen;
    }
    internal LocalApiResponse? LocalRequest(string method, string path, JObject body)
    {
        if (path != CrewLocalApi.Path && !path.StartsWith(CrewLocalApi.Path + "/", StringComparison.Ordinal)) return null;
        JObject Status()
        {
            var value = local?.Status() ?? new JObject { ["scope_id"] = JValue.CreateNull(), ["targets"] = new JArray(), ["history"] = new JArray() };
            value["enabled"] = enabled(); value["active"] = active; value["failed"] = failed;
            value["connection_id"] = settings?.Current.ConnectionId; value["window_open"] = window?.IsOpen == true;
            value["notification"] = presentation?.Status(); return value;
        }
        try
        {
            if (method == "GET" && path == CrewLocalApi.Path) return new LocalApiResponse(200, Status());
            if (method == "PATCH" && path == CrewLocalApi.Path)
            {
                if (body.Count != 1 || body["enabled"]?.Type != JTokenType.Boolean) throw new LocalApiException(400, "invalid_request");
                var value = (bool)body["enabled"]!; saveEnabled(value); if (!value) Suspend();
                return new LocalApiResponse(200, Status());
            }
            if (local == null) return LocalApiResponse.Error(409, "no_active_reports");
            if (method == "POST" && path == CrewLocalApi.Path + "/stop") return local.Dispatch(method, path, body);
            if (!active || failed) return LocalApiResponse.Error(409, failed ? "reports_failed" : "reports_disabled");
            if (method == "POST" && path == CrewLocalApi.Path + "/window")
            {
                local.RequireScope(body, "open");
                if (body["open"]?.Type != JTokenType.Boolean) throw new LocalApiException(400, "invalid_request");
                if ((bool)body["open"]! != (window?.IsOpen == true))
                    try { ToggleWindow(); } catch (Exception error) { Fail(error); throw; }
                return new LocalApiResponse(200, Status());
            }
            if (method == "GET" && path == CrewLocalApi.Path + "/situation")
                return new LocalApiResponse(200, settings!.Current.Information.Filter(source!.Capture(reports!.Targets)));
            return local.Dispatch(method, path, body);
        }
        catch (CrewException error) { log(error); return LocalApiResponse.Error(400, error.Code); }
    }
    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception error) { Fail(error); }
    }
    private void Fail(Exception error)
    {
        log(error); failed = true;
        Cleanup(() => reports?.SetPaused(true, clock.Elapsed.TotalSeconds), () => presentation?.Clear(), () => window?.Stop());
        if (launcher != null) launcher.Control.ToolTip = "AI Crew - Reports / " + error.GetType().Name;
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
        Suspend(); var oldReports = reports; reports = null; Cleanup(() => oldReports?.Dispose()); local = null; source = null; connections = null; settings = null;
        model = null; root = null; view = null; mission = null; player = null; failed = false; nextRefresh = 0;
    }
}
