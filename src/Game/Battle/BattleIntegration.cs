using System;
using System.IO;
using System.Xml;
using Noesis;
using SeaPower;
using SeapowerUI;
using SeapowerUI.ViewModels;

namespace SeaPowerAIAdvisor;

internal sealed class BattleIntegration
{
    private readonly Action<Action> guard;
    private readonly AdvisorRuntime runtime;
    private readonly Action<string> warn;
    private readonly AdvisorOptionsStore options;
    private readonly AdvisorBackendRegistry backends;
    private readonly BattleSituationReader reader = new();
    private MainGameViewModel? model;
    private MainGameView? view;
    private FrameworkElement? uiRoot;
    private object? mission;
    private object? player;
    private AdvisorHudButton? launcher;
    private AdvisorWindow? advisor;
    private BattleContactAssessments? contactAssessments;
    private AdvisorErrorWindow? errorWindow;
    private Action? showError;

    internal BattleIntegration(AdvisorRuntime runtime, Action<string> warn, AdvisorOptionsStore options, AdvisorBackendRegistry backends)
    { this.runtime = runtime; guard = runtime.Guard; this.warn = warn; this.options = options; this.backends = backends; }

    internal void Tick(MainGameViewModel current)
    {
        Plugin.LocalApi?.Detach(this);
        if (current.RootControl == null) return;
        if (!runtime.Failed && (!SceneCreator.InstanceExists(false) ||
            !SceneCreator.Instance.IsLoadingDone || Globals._playerTaskforce == null)) return;
        var root = ReferenceEquals(model, current) && ReferenceEquals(uiRoot, current.RootControl) ? view : FindView(current.RootControl);
        if (root == null || root.ActualWidth <= 0 || root.ActualHeight <= 0) return;
        var currentMission = runtime.Failed ? mission : SceneCreator.Instance;
        var currentPlayer = runtime.Failed ? player : Globals._playerTaskforce;
        if (!ReferenceEquals(model, current) || !ReferenceEquals(uiRoot, current.RootControl) || !ReferenceEquals(mission, currentMission) ||
            !ReferenceEquals(player, currentPlayer))
        {
            Reset();
            model = current; view = root; uiRoot = current.RootControl; mission = currentMission; player = currentPlayer;
        }
        if (launcher == null)
        {
            var grid = root.FindName("DisplayAreaGrid") as Grid
                ?? throw new InvalidOperationException("The battle HUD grid is unavailable.");
            var reference = root.FindName("UnitStatusWindow") as ToggleButton
                ?? throw new InvalidOperationException("The battle HUD toggle button is unavailable.");
            var button = new AdvisorHudButton(grid, reference, AdvisorText.Load(GameLanguage.Read())["battle.launcher"]);
            launcher = button;
            void OpenAdvisor()
            {
                var currentAdvisor = GetAdvisor();
                currentAdvisor.Toggle();
                button.Control.IsChecked = currentAdvisor.IsOpen;
            }
            void ShowError()
            {
                var host = FindHost(current.RootControl) ?? throw new InvalidOperationException("The battle window host is unavailable.");
                errorWindow ??= new AdvisorErrorWindow(root, host, runtime, AdvisorText.Load(GameLanguage.Read()),
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
        launcher.Control.Visibility = current.IsHudVisible ? Visibility.Visible : Visibility.Collapsed;
        if (!runtime.Failed)
        {
            var host = FindHost(current.RootControl) ?? throw new InvalidOperationException("The battle window host is unavailable.");
            contactAssessments ??= new BattleContactAssessments(GetAdvisor, () => options.Current.Information, guard);
            contactAssessments.Tick(current.RootControl, host);
            advisor?.Tick();
            if (options.Current.DeveloperApiEnabled) Plugin.LocalApi?.Attach(this, "tactical_battle", GetAdvisor);
        }
        if (showError != null) runtime.NotifyFailure(showError);
        errorWindow?.Tick();
        launcher.Control.IsChecked = errorWindow?.IsOpen == true || advisor?.IsOpen == true;
    }

    private AdvisorWindow GetAdvisor()
    {
        if (view == null || model?.RootControl == null) throw new InvalidOperationException("The battle advisor view is unavailable.");
        var host = FindHost(model.RootControl) ?? throw new InvalidOperationException("The battle window host is unavailable.");
        return advisor ??= new AdvisorWindow(view, host, guard, new BattleAdvisorSource(Capture), GameLanguage.Read, warn, options, backends,
            new BattleUnitNavigation(() => model?.Map, () => this.view));
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
        var xmlBriefing = "";
        var briefingUnavailable = false;
        var briefingFromBase = false;
        try
        {
            var files = FileManager.Instance;
            // Read only public pane-selection keys and source-file metadata, never unit definitions or triggers.
            var ini = SceneCreator.Instance.MissionIni;
            string? ResolveFile(string path)
            {
                var resolved = System.IO.Path.IsPathRooted(path) ? path : files.GetFile(path, null);
                return !string.IsNullOrEmpty(resolved) && File.Exists(resolved) ? resolved : null;
            }
            var briefing = BattleBriefingReader.Read(files.GetRelativePath(Globals.currentMissionFilePath) ?? "", GameLanguage.Read(),
                (section, key, fallback) => ini.readValue(section, key, fallback, true), ResolveFile, File.ReadAllText,
                () =>
                {
                    var baseFile = ini.readValue("File", "BaseFile", "", false);
                    if (string.IsNullOrWhiteSpace(baseFile)) return null;
                    var basePath = ResolveFile(baseFile)
                        ?? throw new FileNotFoundException("The saved mission's BaseFile is unavailable.", baseFile);
                    var original = Utils.openIniFile(basePath, true, true)
                        ?? throw new InvalidDataException("The original mission's briefing settings could not be opened.");
                    return new BattleBriefingTemplate(files.GetRelativePath(basePath) ?? "",
                        (section, key, fallback) => original.readValue(section, key, fallback, true));
                });
            xmlBriefing = briefing.Text;
            briefingFromBase = briefing.FromBase;
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
            error is XmlException || error is NotSupportedException || error is InvalidDataException)
        {
            briefingUnavailable = true;
            warn("Public mission briefing unavailable: " + error);
        }
        return reader.Capture(map.MapKnownUnits.AllUnits, player!, MissionManager.Instance.Objectives, EventLog.Instance,
            now, SceneCreator.Instance.CurrentMissionName,
            (bool)BattleValue.Member(typeof(Globals).GetProperty("AllowControlOfEnemyUnits")!.GetValue(null)!, "Value")!,
            new BattleCapabilityReader(references), xmlBriefing, briefingUnavailable, briefingFromBase);
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

    internal void Stop()
    {
        Plugin.LocalApi?.Detach(this);
        contactAssessments?.Suspend();
        var previous = advisor; advisor = null;
        previous?.Stop();
    }

    internal void Reset()
    {
        try { Stop(); }
        finally
        {
            var previous = launcher; var report = errorWindow;
            var contacts = contactAssessments; contactAssessments = null;
            advisor = null; errorWindow = null; showError = null; launcher = null; model = null; view = null; uiRoot = null; mission = null; player = null;
            try { contacts?.Dispose(); }
            finally
            {
                try { report?.Close(); }
                finally { previous?.Dispose(); }
            }
        }
    }
}
