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
    private readonly AdvisorOptionsStore options;
    private readonly BattleSituationReader reader = new();
    private MainGameViewModel? model;
    private MainGameView? view;
    private FrameworkElement? uiRoot;
    private object? mission;
    private object? player;
    private AdvisorHudButton? launcher;
    private AdvisorWindow? advisor;

    internal BattleIntegration(Action<Action> guard, Action<string> warn, AdvisorOptionsStore options) { this.guard = guard; this.warn = warn; this.options = options; }

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
            var grid = root.FindName("DisplayAreaGrid") as Grid
                ?? throw new InvalidOperationException("The battle HUD grid is unavailable.");
            var reference = root.FindName("UnitStatusWindow") as ToggleButton
                ?? throw new InvalidOperationException("The battle HUD toggle button is unavailable.");
            var button = new AdvisorHudButton(grid, reference, AdvisorText.Load(GameLanguage.Read())["battle.launcher"]);
            launcher = button;
            button.Control.Click += (_, _) => guard(() =>
            {
                var host = FindHost(current.RootControl) ?? throw new InvalidOperationException("The battle window host is unavailable.");
                advisor ??= new AdvisorWindow(root, host, guard, new BattleAdvisorSource(Capture), GameLanguage.Read, warn, options);
                advisor.Toggle();
                button.Control.IsChecked = advisor.IsOpen;
            });
        }
        launcher.Control.Visibility = current.IsHudVisible ? Visibility.Visible : Visibility.Collapsed;
        advisor?.Tick();
        launcher.Control.IsChecked = advisor?.IsOpen == true;
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
        var now = SeaPower.Environment.Instance.DateTime;
        var references = ReadReferenceCatalog(now.Year);
        return reader.Capture(map.MapKnownUnits.AllUnits, player!, MissionManager.Instance.Objectives, EventLog.Instance,
            now, SceneCreator.Instance.CurrentMissionName,
            (bool)BattleValue.Member(typeof(Globals).GetProperty("AllowControlOfEnemyUnits")!.GetValue(null)!, "Value")!,
            new BattleCapabilityReader(references));
    }

    private static BattleReferenceCatalog ReadReferenceCatalog(int year)
    {
        var type = typeof(Globals).Assembly.GetType("SeaPower.EncyclopedicDataManager", true)!;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy;
        var exists = type.GetMethod("InstanceExists", flags) ?? throw new MissingMethodException(type.FullName, "InstanceExists");
        if (!(bool)exists.Invoke(null, new object[] { false })!)
            return new BattleReferenceCatalog(Array.Empty<object>(), Array.Empty<object>(), year, false);
        var manager = type.GetProperty("Instance", flags)?.GetValue(null)
            ?? throw new InvalidOperationException("The encyclopedia instance is unavailable.");
        if (!(bool)BattleValue.Required(manager, "UnitsLoaded"))
            return new BattleReferenceCatalog(Array.Empty<object>(), Array.Empty<object>(), year, false);
        return new BattleReferenceCatalog(BattleValue.Rows(BattleValue.Required(manager, "MuseumObjectEntries")),
            BattleValue.Rows(BattleValue.Required(manager, "WeaponMuseumEntries")), year, true);
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
            var previous = launcher;
            advisor = null; launcher = null; model = null; view = null; uiRoot = null; mission = null; player = null;
            previous?.Dispose();
        }
    }
}
