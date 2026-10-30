using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
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
    internal ToggleButton? HudControl => launcher?.Control;
    private AdvisorWindow? advisor;
    private AdvisorOperations? operations;
    private AdvisorOperationGateway? operationGateway;
    private AdvisorUnitReservations? reservations;
    private BattleContactAssessments? contactAssessments;
    private BattleMapNavigation? mapNavigation;
    private BattleMapPointInput? mapInput;
    private AdvisorErrorWindow? errorWindow;
    private Action? showError;

    internal BattleIntegration(AdvisorRuntime runtime, Action<string> warn, AdvisorOptionsStore options, AdvisorBackendRegistry backends)
    { this.runtime = runtime; guard = runtime.Guard; this.warn = warn; this.options = options; this.backends = backends; }

    internal void Tick(MainGameViewModel current)
    {
        Plugin.LocalApi?.Detach(this);
        if (current.RootControl == null) { ClearOperations(); return; }
        if (!runtime.Failed && (!SceneCreator.InstanceExists(false) ||
            !SceneCreator.Instance.IsLoadingDone || Globals._playerTaskforce == null))
        { ClearOperations(); return; }
        var root = ReferenceEquals(model, current) && ReferenceEquals(uiRoot, current.RootControl) ? view : GameUi.FindVisual<MainGameView>(current.RootControl);
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
                var host = GameUi.FindWindowHost(current.RootControl) ?? throw new InvalidOperationException("The battle window host is unavailable.");
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
            var host = GameUi.FindWindowHost(current.RootControl) ?? throw new InvalidOperationException("The battle window host is unavailable.");
            mapInput ??= new BattleMapPointInput(root, () => model?.Map?.DisplayMap,
                () => model?.StateManager.CurrentState.Value, () => advisor?.CanInsertLocation ?? !QuestionInputController.IsComposing,
                coordinate => GetAdvisor().InsertLocation(coordinate), guard);
            contactAssessments ??= new BattleContactAssessments(GetAdvisor, () => options.Current.Information, guard);
            contactAssessments.Tick(current.RootControl, host);
            mapNavigation?.Tick();
            EnsureOperations();
            operationGateway?.Pump();
            advisor?.Tick();
            if (options.Current.DeveloperApiEnabled)
            {
                Plugin.LocalApi?.Attach(this, "tactical_battle", GetAdvisor, operations, reservations);
            }
        }
        if (showError != null) runtime.NotifyFailure(showError);
        errorWindow?.Tick();
        launcher.Control.IsChecked = errorWindow?.IsOpen == true || advisor?.IsOpen == true;
        launcher.SetStatus(advisor?.IsConsulting == true, advisor?.HasUnreadResult == true);
    }

    private AdvisorWindow GetAdvisor()
    {
        if (view == null || model?.RootControl == null) throw new InvalidOperationException("The battle advisor view is unavailable.");
        var host = GameUi.FindWindowHost(model.RootControl) ?? throw new InvalidOperationException("The battle window host is unavailable.");
        mapNavigation ??= new BattleMapNavigation(() => model?.Map);
        EnsureOperations();
        if (advisor == null)
        {
            advisor = new AdvisorWindow(view, host, guard, new BattleAdvisorSource(Capture, () => operationGateway), GameLanguage.Read, warn, options, backends,
                new BattleUnitNavigation(() => model?.Map, () => this.view), new BattleWeaponNavigation(() => this.view, guard), mapNavigation);
        }
        return advisor;
    }

    private AdvisorOperationGateway EnsureOperations()
    {
        if (operationGateway == null)
        {
            operations = new AdvisorOperations(id => options.Current.IsCommandEnabled(id));
            mapNavigation ??= new BattleMapNavigation(() => model?.Map);
            var support = new BattleOperationSupport(OperationObjects, () => options.Current.Information, OperationKnownUnits);
            foreach (var command in AdvisorOperationCatalog.BuiltIn.CreateOperations(support, mapNavigation)) operations.Register(command);
            operationGateway = new AdvisorOperationGateway(operations);
            reservations = new AdvisorUnitReservations(operations, () => OperationObjects()
                .Select(unit => (IAdvisorReservationUnit)new BattleReservationUnit(unit)).ToArray());
        }
        return operationGateway;
    }
    private void ClearOperations()
    {
        var previous = operationGateway; operationGateway = null; operations = null; reservations = null;
        previous?.Dispose();
    }

    private IReadOnlyList<ObjectBase> OperationObjects()
    {
        if (runtime.Failed || model == null || !ReferenceEquals(model.RootControl, uiRoot) ||
            !SceneCreator.InstanceExists(false) || !ReferenceEquals(mission, SceneCreator.Instance) ||
            !SceneCreator.Instance.IsLoadingDone || !ReferenceEquals(player, Globals._playerTaskforce))
            throw new AdvisorOperationException(409, "battle_unavailable");
        return Globals._playerTaskforce.TaskforceObjects.OfType<ObjectBase>()
            .Where(unit => unit != null && !unit.IsDestroyed).ToArray();
    }

    private static bool EnemyControlEnabled =>
        (bool)BattleValue.Required(typeof(Globals).GetProperty("AllowControlOfEnemyUnits")!.GetValue(null)!, "Value");
    private IReadOnlyList<MapUnitViewModel> OperationKnownUnits()
    {
        OperationObjects();
        if (EnemyControlEnabled) throw new SeaPowerAIAdvisor.Api.OperationException(403, "battle_disclosure");
        return model!.Map.MapKnownUnits.AllUnits.ToArray();
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
        return reader.Capture(map.MapKnownUnits.AllUnits, player!, MissionManager.Instance.Objectives, MissionManager.Instance.OnMapAreas, EventLog.Instance,
            now, SceneCreator.Instance.CurrentMissionName,
            EnemyControlEnabled,
            new BattleCapabilityReader(references, Atmosphere.SpeedOfSound), xmlBriefing, briefingUnavailable, briefingFromBase);
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

    internal void Destroy(MainGameViewModel current)
    {
        if (ReferenceEquals(model, current)) Reset();
    }

    internal void Stop()
    {
        AdvisorRuntime.CleanupAll(
            () => Plugin.LocalApi?.Detach(this),
            ClearOperations,
            () => { var input = mapInput; mapInput = null; input?.Dispose(); },
            () => contactAssessments?.Suspend(),
            () => { var previous = advisor; advisor = null; previous?.Stop(); },
            () => { var navigation = mapNavigation; mapNavigation = null; navigation?.Dispose(); });
    }

    internal void Reset()
    {
        try { Stop(); }
        finally
        {
            var previous = launcher; var report = errorWindow;
            var contacts = contactAssessments; contactAssessments = null;
            advisor = null; operations = null; errorWindow = null; showError = null; launcher = null; model = null; view = null; uiRoot = null; mission = null; player = null;
            try { contacts?.Dispose(); }
            finally
            {
                try { report?.Close(); }
                finally { previous?.Dispose(); }
            }
        }
    }
}
