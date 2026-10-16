using System;
using Noesis;
using SeaPower;
using SeapowerUI;
using SeapowerUI.ViewModels;

namespace SeaPowerAIAdvisor;

internal sealed class BattleIntegration
{
    private readonly Action<Action> guard;
    private readonly Action<string> warn;
    private readonly BattleSituationReader reader = new();
    private MainGameViewModel? model;
    private MainGameView? view;
    private FrameworkElement? uiRoot;
    private object? mission;
    private object? player;
    private Grid? overlay;
    private ToggleButton? launcher;
    private AdvisorWindow? advisor;

    internal BattleIntegration(Action<Action> guard, Action<string> warn) { this.guard = guard; this.warn = warn; }

    internal void Tick(MainGameViewModel current)
    {
        if (current.RootControl == null || !SceneCreator.InstanceExists(false) ||
            !SceneCreator.Instance.IsLoadingDone || Globals._playerTaskforce == null) return;
        var root = ReferenceEquals(model, current) && ReferenceEquals(uiRoot, current.RootControl) ? view : FindView(current.RootControl);
        if (root == null || root.ActualWidth <= 0 || root.ActualHeight <= 0) return;
        if (!ReferenceEquals(model, current) || !ReferenceEquals(uiRoot, current.RootControl) || !ReferenceEquals(mission, SceneCreator.Instance) ||
            !ReferenceEquals(player, Globals._playerTaskforce))
        {
            Reset();
            model = current; view = root; uiRoot = current.RootControl; mission = SceneCreator.Instance; player = Globals._playerTaskforce;
        }
        if (launcher == null)
        {
            overlay = root.OverlayCanvasNew ?? throw new InvalidOperationException("The battle overlay grid is unavailable.");
            launcher = new ToggleButton
            {
                Content = "AI", ToolTip = AdvisorText.Load(GameLanguage.Read())["battle.launcher"],
                Width = 44, Height = 30, FontSize = 15,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 100, 16, 0)
            };
            Panel.SetZIndex(launcher, 100);
            launcher.Click += (_, _) => guard(() =>
            {
                var host = FindHost(current.RootControl) ?? throw new InvalidOperationException("The battle window host is unavailable.");
                advisor ??= new AdvisorWindow(root, host, guard, new BattleAdvisorSource(Capture), GameLanguage.Read, warn);
                advisor.Toggle();
                launcher.IsChecked = advisor.IsOpen;
            });
            overlay.Children.Add(launcher);
        }
        launcher.Visibility = current.IsHudVisible ? Visibility.Visible : Visibility.Collapsed;
        advisor?.Tick();
        launcher.IsChecked = advisor?.IsOpen == true;
    }

    private BattleSnapshot Capture()
    {
        if (model == null || view == null || !ReferenceEquals(model.RootControl, uiRoot) ||
            !SceneCreator.InstanceExists(false) || !ReferenceEquals(mission, SceneCreator.Instance) ||
            !SceneCreator.Instance.IsLoadingDone || !ReferenceEquals(player, Globals._playerTaskforce) ||
            !MissionManager.InstanceExists(false) || !EventLog.InstanceExists(false) ||
            !SeaPower.Environment.InstanceExists(false))
            throw new SituationUnavailableException("battle_unavailable");
        var map = model.Map ?? throw new InvalidOperationException("The player's battle map is unavailable.");
        return reader.Capture(map.MapKnownUnits.AllUnits, player!, MissionManager.Instance.Objectives, EventLog.Instance,
            SeaPower.Environment.Instance.DateTime, SceneCreator.Instance.CurrentMissionName,
            (bool)BattleSituationReader.Member(typeof(Globals).GetProperty("AllowControlOfEnemyUnits")!.GetValue(null)!, "Value")!);
    }

    private static MainGameView? FindView(DependencyObject node)
    {
        if (node is MainGameView game) return game;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var found = FindView(VisualTreeHelper.GetChild(node, i));
            if (found != null) return found;
        }
        return null;
    }

    private static WindowHost? FindHost(DependencyObject node)
    {
        if (node is WindowHost host) return host;
        if (node is FrameworkElement root)
        {
            if (root.FindName("WindowHost") is WindowHost named) return named;
            // WindowHost is an attached behavior, so it need not appear as a visual child or have a name.
            foreach (var behavior in Noesis.Interactivity.Interaction.GetBehaviors(root))
                if (behavior is WindowHost attached) return attached;
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var found = FindHost(VisualTreeHelper.GetChild(node, i));
            if (found != null) return found;
        }
        return null;
    }

    internal void Destroy(MainGameViewModel current)
    {
        if (ReferenceEquals(model, current)) Reset();
    }

    internal void Reset()
    {
        try { advisor?.Close(); }
        finally
        {
            if (overlay != null && launcher != null) overlay.Children.Remove(launcher);
            advisor = null; launcher = null; overlay = null; model = null; view = null; uiRoot = null; mission = null; player = null;
        }
    }
}
