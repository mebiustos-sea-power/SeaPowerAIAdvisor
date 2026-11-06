using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Xml.Linq;
using Noesis;
using SeapowerUI;
using SeaPowerAIAdvisor.Shared;

namespace SeaPowerAIAdvisor.Crew;

// This window owns drafts and presentation. Closing it never stops the speaking session.
internal sealed class CrewWindow : IDisposable
{
    private readonly FrameworkElement view, root;
    private readonly WindowHost host;
    private readonly Window window;
    private readonly CrewChatter chatter;
    private readonly CrewSettingsStore settings;
    private readonly ICrewConnections connections;
    private readonly CrewText text;
    private readonly Func<string, string> common;
    private readonly Action<Action> guard;
    private readonly Action<Exception> log;
    private readonly Func<double> now;
    private readonly TypingScope typing = new();
    private readonly AiConnectionsView editor;
    private readonly SettingsTabNavigation tabs, apiTabs, libraryTabs;
    private readonly ComboBox selection, notificationPosition;
    private readonly TextBox interval, duration, exchangeChance;
    private readonly CheckBox showHelpTab, showRanks, autoAppointments;
    private readonly CrewPortraits portraits;
    private readonly CrewPersonEditor personEditor;
    private readonly CrewCharacterLibrary characters;
    private readonly CrewCharacterLibraryView characterView, characterPicker;
    private string? assigningTarget;
    private readonly CrewPortraitLibraryView libraryView, portraitPicker;
    private readonly UiNotice settingsNotice, chatterNotice;
    private readonly CrewTargetSelection targetSelection = new();
    private readonly Dictionary<string, CheckBox> informationChoices = new(StringComparer.Ordinal);
    private CrewInformation information = CrewInformation.Default;
    private string[] ids = Array.Empty<string>();
    private string? page, baseline;
    private bool changing, stopped;
    private int revision = -1, libraryRevision = -1;
    private bool choosingPortrait;
    private Action<string?>? portraitSelected;
    private CrewUsageSnapshot? displayedUsage;
    private Action? confirmed;
    private CancellationTokenSource? testingCancellation;
    private Task<string>? testing;
    internal IEnumerable<string?> DisplayPortraitIds => new[] { personEditor.PortraitId }.Concat(libraryView.DisplayedIds).Concat(portraitPicker.DisplayedIds).Concat(characterView.DisplayedIds).Concat(characterPicker.DisplayedIds);
    internal int PortraitRevision => personEditor.PortraitRevision + libraryView.PortraitRevision + portraitPicker.PortraitRevision + characterView.PortraitRevision + characterPicker.PortraitRevision;
    internal bool IsOpen => host.Windows.Contains(window);
    private T Find<T>(string name) where T : class => root.FindName(name) as T ?? throw new InvalidOperationException("Missing Chatter control: " + name);
    private string? SelectedId => selection.SelectedIndex <= 0 ? null : ids[selection.SelectedIndex - 1];
    private string Values => page switch
    {
        "chatter" => interval.Text + ":" + duration.Text + ":" + notificationPosition.SelectedIndex + ":" + showHelpTab.IsChecked + ":" + exchangeChance.Text + ":" + showRanks.IsChecked,
        "appointments" => autoAppointments.IsChecked.ToString(),
        "information" => information.Key,
        _ => SelectedId ?? ""
    };
    private bool Dirty => characterView.HasChanges || personEditor.HasChanges || libraryView.HasChanges || portraitPicker.HasChanges || targetSelection.Dirty || (page == "api" && apiTabs.Current == 1 ? editor.HasChanges : page != null && Values != baseline);

    internal CrewWindow(FrameworkElement view, WindowHost host, CrewChatter chatter, CrewSettingsStore settings, ICrewConnections connections,
        Func<string, string> common, CrewText text, CrewPortraits portraits, CrewCharacterLibrary characters, Func<double> now, Action<Action> guard, Action<Exception> log)
    {
        this.view = view; this.host = host; this.chatter = chatter; this.settings = settings; this.connections = connections;
        this.common = common; this.text = text; this.portraits = portraits; this.characters = characters; this.now = now; this.guard = guard; this.log = log;
        using var source = typeof(CrewWindow).Assembly.GetManifestResourceStream("SeaPowerAIAdvisor.CrewWindow.xaml") ?? throw new FileNotFoundException("The Chatter view is missing.");
        using var reader = new StreamReader(source);
        root = GUI.ParseXaml(reader.ReadToEnd()) as FrameworkElement ?? throw new InvalidOperationException("The Chatter view is invalid.");
        foreach (var item in text.Values) root.Resources["Crew." + item.Key] = item.Value;
        root.Resources["Crew.save"] = common("settings.save_form");
        root.Resources["Crew.cancel"] = common("settings.cancel");
        root.Resources["Crew.back_categories"] = common("settings.back_categories");
        root.Resources["Crew.connection_selection"] = common("settings.selection_tab");
        root.Resources["Crew.registration"] = common("settings.registration_tab");
        root.Resources["Crew.restore_defaults"] = common("information.restore_defaults");
        UiResources.Apply(root);
        Find<ContentControl>("HelpContent").Content = GUI.ParseXaml(MarkdownRenderer.Render(text["help_body"]).ToString(SaveOptions.DisableFormatting)) as FrameworkElement
            ?? throw new InvalidOperationException("The Chatter help view is invalid.");
        settingsNotice = new UiNotice(Find<TextBlock>("SettingsStatus"), root);
        chatterNotice = new UiNotice(Find<TextBlock>("ChatterStatus"), root);
        selection = Find<ComboBox>("Connection"); notificationPosition = Find<ComboBox>("NotificationPosition");
        foreach (CrewNotificationPosition position in Enum.GetValues(typeof(CrewNotificationPosition)))
            notificationPosition.Items.Add(new ComboBoxItem { Content = text["position." + CrewSettings.PositionCode(position)] });
        interval = Find<TextBox>("Interval"); duration = Find<TextBox>("Duration"); exchangeChance = Find<TextBox>("ExchangeChance"); showHelpTab = Find<CheckBox>("ShowHelpTab"); showRanks = Find<CheckBox>("ShowRanks");
        autoAppointments = Find<CheckBox>("AutoAppointments");
        var fixedInformation = Find<FrameworkElement>("InformationFixed");
        fixedInformation.Loaded += (_, _) => Run(() => UiResources.ApplyDisabledCheckBoxBrushes(fixedInformation));
        foreach (var item in CrewInformation.Items)
        {
            var label = new StackPanel();
            label.Children.Add(new TextBlock { Text = text["information." + item.Id], TextWrapping = TextWrapping.Wrap });
            label.Children.Add(new TextBlock { Text = text["information." + item.Id + "_hint"], TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7f, Margin = new Thickness(0, 3, 0, 0) });
            var box = new CheckBox { Content = label, HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 4, 0, 8) };
            box.Click += (_, _) => Run(() => information = information.With(item.Id, box.IsChecked == true));
            informationChoices.Add(item.Id, box); Find<StackPanel>("InformationChoices").Children.Add(box);
        }
        var form = AiConnectionsView.Create(common); Find<ContentControl>("Registration").Content = form;
        editor = new AiConnectionsView(form, connections.Store, () => connections.Providers, common, GameLanguage.Read, typing, Run,
            CanCommit, (name, action) => Confirm(action, string.Format(CultureInfo.CurrentCulture, common("settings.delete_question"), name), common("profile.remove"), common("settings.cancel")),
            action => Confirm(action, common("connection.duplicate_discard_question"), common("settings.duplicate")),
            error => Notice(connections.Error(error), true), role: connections.Role);
        editor.Saved += () => { RefreshConnections(); Saved(); };
        editor.Changed += () => { if (testing == null) TestNotice(""); };
        editor.Find<Button>("TestConnection").Click += (_, _) => Run(Test);
        tabs = Navigation("Tabs"); apiTabs = Navigation("ApiTabs"); libraryTabs = Navigation("LibraryTabs");
        Bind("OpenConnections", () => OpenPage("api")); Bind("OpenChatter", () => OpenPage("chatter"));
        Bind("OpenInformation", () => OpenPage("information"));
        Bind("OpenAppointments", () => OpenPage("appointments"));
        Bind("ResetInformation", () => { SetInformation(CrewInformation.Default); settingsNotice.Show(""); });
        Bind("Back", () => Navigate(Home));
        Bind("AppointAll", () =>
        {
            if (!CanCommit() || chatter.Paused || chatter.Busy) return;
            connections.ValidateSelection(settings.Current.ConnectionId);
            chatter.RequestAppointments(now()); DrawTargets(); Notice("");
        });
        Bind("AllTargetsOn", () => { SyncSelection(); targetSelection.SetAll(true); DrawTargets(); });
        Bind("AllTargetsOff", () => { SyncSelection(); targetSelection.SetAll(false); DrawTargets(); });
        Bind("ApplyTargets", () =>
        {
            if (!CanCommit()) return;
            SyncSelection(); var selected = targetSelection.Selected;
            if (selected.Any(id => !chatter.Enabled(id))) connections.ValidateSelection(settings.Current.ConnectionId);
            chatter.ApplyTargets(selected, now()); targetSelection.Discard(); DrawTargets(); Notice("");
        });
        Bind("PauseChatter", () =>
        {
            if (chatter.Paused && (chatter.HasEnabledTargets || chatter.HasPendingAppointments)) connections.ValidateSelection(settings.Current.ConnectionId);
            chatter.SetPaused(!chatter.Paused, now());
        });
        Bind("SaveConnection", () =>
        {
            if (!CanCommit()) return;
            var saved = settings.Current; settings.Save(new CrewSettings(SelectedId, saved.IntervalMinutes, saved.DisplaySeconds, saved.NotificationPosition, saved.ShowHelpTab, saved.ExchangeChancePercent, saved.Information, saved.ShowRanks, saved.AutoAppointments));
            baseline = Values; editor.UpdateCards(); Saved();
        });
        Bind("SaveChatter", () =>
        {
            if (!CanCommit()) return;
            int Read(TextBox input) => int.TryParse(input.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : throw new CrewException("settings_invalid");
            settings.Save(new CrewSettings(settings.Current.ConnectionId, Read(interval), Read(duration), (CrewNotificationPosition)notificationPosition.SelectedIndex, showHelpTab.IsChecked == true, Read(exchangeChance), settings.Current.Information, showRanks.IsChecked == true, settings.Current.AutoAppointments));
            UpdateHelpTab(); revision = -1;
            baseline = Values; Saved();
        });
        Bind("SaveInformation", () =>
        {
            if (!CanCommit()) return;
            var saved = settings.Current;
            settings.Save(new CrewSettings(saved.ConnectionId, saved.IntervalMinutes, saved.DisplaySeconds, saved.NotificationPosition, saved.ShowHelpTab, saved.ExchangeChancePercent, information, saved.ShowRanks, saved.AutoAppointments));
            baseline = Values; Saved();
        });
        Bind("SaveAppointments", () =>
        {
            if (!CanCommit()) return;
            var saved = settings.Current;
            settings.Save(new CrewSettings(saved.ConnectionId, saved.IntervalMinutes, saved.DisplaySeconds, saved.NotificationPosition, saved.ShowHelpTab, saved.ExchangeChancePercent, saved.Information, saved.ShowRanks, autoAppointments.IsChecked == true));
            baseline = Values; Saved();
        });
        var gallery = CrewPortraitLibraryView.Create(); Find<ContentControl>("PortraitLibrary").Content = gallery;
        libraryView = new CrewPortraitLibraryView(gallery, root, portraits, text, common, Run,
            (action, message, caption) => Confirm(action, message, caption, common("settings.cancel")), log);
        var picker = CrewPortraitLibraryView.Create(); Find<ContentControl>("PortraitPicker").Content = picker;
        portraitPicker = new CrewPortraitLibraryView(picker, root, portraits, text, common, Run,
            (action, message, caption) => Confirm(action, message, caption, common("settings.cancel")), log,
            select: id => { var selected = portraitSelected; ClosePortraitPicker(); selected?.Invoke(id); },
            back: BackPortraitPicker);
        personEditor = new CrewPersonEditor(root, chatter, portraits, text, common,
            typing, CanCommit, Run, action => Confirm(action), OpenPortraitPicker);
        var characterForm = CrewCharacterLibraryView.Create(); Find<ContentControl>("CharacterLibrary").Content = characterForm;
        characterView = new CrewCharacterLibraryView(characterForm, root, characters, portraits, text, common, typing, CanCommit, Run,
            (action, message, caption) => Confirm(action, message, caption, common("settings.cancel")), OpenPortraitPicker, log);
        var characterChoice = CrewCharacterLibraryView.Create(); Find<ContentControl>("CharacterPicker").Content = characterChoice;
        characterPicker = new CrewCharacterLibraryView(characterChoice, root, characters, portraits, text, common, typing, CanCommit, Run,
            (action, message, caption) => Confirm(action, message, caption, common("settings.cancel")), OpenPortraitPicker, log,
            select: AssignCharacter, back: CloseCharacterPicker,
            canSelect: character => assigningTarget != null && !chatter.Busy && (!chatter.Paused || character.IsComplete) && chatter.CanAssignCharacter(assigningTarget, character));
        Bind("CancelConfirmation", HideConfirmation);
        Bind("Confirm", () => { var action = confirmed; HideConfirmation(); action?.Invoke(); });
        foreach (var input in new[] { interval, duration, exchangeChance })
        {
            input.GotKeyboardFocus += (_, _) => Run(() => typing.Acquire(input));
            input.LostKeyboardFocus += (_, _) => Run(() => typing.Release(input));
        }
        var width = Math.Min(760, view.ActualWidth - 40); var height = Math.Min(620, view.ActualHeight - 80);
        window = new Window { Header = "AI Crew - " + text["chatter"], Content = root, Width = width, Height = height,
            MinWidth = Math.Min(420, width), MinHeight = Math.Min(300, height), MaxWidth = view.ActualWidth - 20, MaxHeight = view.ActualHeight - 40,
            X = (float)Math.Max(10, (view.ActualWidth - width) / 2), Y = 60, Draggable = true, CanResize = true,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        window.DataContext = window;
        window.Template = view.TryFindResource("WindowLayoutNoScroll") as ControlTemplate ?? throw new InvalidOperationException("The game window template is unavailable.");
        window.ButtonsTemplate = root.Resources["CrewWindowButtons"] as DataTemplate ?? throw new InvalidOperationException("The Chatter window buttons are unavailable.");
        window.CloseAction = new CloseCommand(() => Run(RequestClose));
        window.OnClosed += (_, _) =>
        {
            personEditor.Close(); libraryView.Close(); characterView.Close(); CloseCharacterPicker(); ClosePortraitPicker(); typing.Release(); CancelTest();
            Find<StackPanel>("Targets").Children.Clear(); Find<StackPanel>("History").Children.Clear();
        };
        Home(); ResetTabs(); apiTabs.Reset(); libraryTabs.Reset(); DrawUsage();
    }
    private SettingsTabNavigation Navigation(string name)
    {
        var control = Find<TabControl>(name);
        var navigation = new SettingsTabNavigation(index => { changing = true; try { control.SelectedIndex = index; } finally { changing = false; } }, () => { });
        control.SelectionChanged += (sender, args) =>
        { if (ReferenceEquals(args.Source, control) && !changing) navigation.Request(control.SelectedIndex); };
        return navigation;
    }
    private void Bind(string name, Action action) => Find<Button>(name).Click += (_, _) => Run(action);
    private void Run(Action action) => guard(() =>
    {
        try { action(); }
        catch (CrewException e) { log(e); Notice(text["error." + e.Code], true); }
        catch (PortraitDataException e) { log(e); Notice(common("profile.error." + e.Code), true); }
        catch (CrewPortraitLibraryException e) { log(e); Notice(text["error." + e.Code], true); }
        catch (AiConnectionException e) { log(e); Notice(connections.Error(e), true); }
    });
    private bool CanCommit()
    {
        if (testing != null) return false;
        if (!GameTextInput.IsComposing) return true;
        Notice(common("profile.ime"), true); return false;
    }
    private void Confirm(Action action, string? message = null, string? caption = null, string? cancel = null)
    {
        typing.Release(); confirmed = action; Find<TextBlock>("ConfirmationText").Text = message ?? common("settings.discard_question");
        Find<Button>("Confirm").Content = caption ?? common("settings.discard_action");
        Find<Button>("CancelConfirmation").Content = cancel ?? common("settings.continue_editing");
        Find<TabControl>("Tabs").IsEnabled = false;
        Find<FrameworkElement>("PortraitPicker").IsEnabled = false;
        Find<FrameworkElement>("CharacterPicker").IsEnabled = false;
        Find<FrameworkElement>("Confirmation").Visibility = Visibility.Visible;
        Find<Button>("CancelConfirmation").Focus();
    }
    private void HideConfirmation() { confirmed = null; Find<FrameworkElement>("Confirmation").Visibility = Visibility.Collapsed; Find<TabControl>("Tabs").IsEnabled = true; Find<FrameworkElement>("PortraitPicker").IsEnabled = true; Find<FrameworkElement>("CharacterPicker").IsEnabled = true; }
    private void Navigate(Action action) { if (Dirty) Confirm(action); else action(); }
    private void Home()
    {
        ClosePortraitPicker(); CloseCharacterPicker(); libraryView.Close(); characterView.Close(); personEditor.Close(); typing.Release(); CancelTest(); editor.Close(); targetSelection.Discard(); revision = -1; page = null; baseline = null; settingsNotice.Show("");
        Find<FrameworkElement>("SettingsHome").Visibility = Visibility.Visible;
        Find<FrameworkElement>("SettingsPage").Visibility = Visibility.Collapsed;
    }
    private void OpenPage(string value)
    {
        page = value; typing.Release(); settingsNotice.Show("");
        Find<TextBlock>("SettingsCategoryTitle").Text = text[value switch { "api" => "connections", "chatter" => "chatter", "information" => "information", "appointments" => "appointments", _ => throw new ArgumentOutOfRangeException(nameof(value)) }];
        Find<FrameworkElement>("SettingsHome").Visibility = Visibility.Collapsed; Find<FrameworkElement>("SettingsPage").Visibility = Visibility.Visible;
        Find<FrameworkElement>("ApiTabs").Visibility = value == "api" ? Visibility.Visible : Visibility.Collapsed;
        Find<FrameworkElement>("ChatterSettings").Visibility = value == "chatter" ? Visibility.Visible : Visibility.Collapsed;
        Find<FrameworkElement>("InformationSettings").Visibility = value == "information" ? Visibility.Visible : Visibility.Collapsed;
        Find<FrameworkElement>("AppointmentSettings").Visibility = value == "appointments" ? Visibility.Visible : Visibility.Collapsed;
        if (value == "api") { apiTabs.Reset(); RefreshConnections(); editor.Open(); }
        else if (value == "chatter")
        {
            var saved = settings.Current;
            interval.Text = saved.IntervalMinutes.ToString(CultureInfo.InvariantCulture);
            exchangeChance.Text = saved.ExchangeChancePercent.ToString(CultureInfo.InvariantCulture);
            duration.Text = saved.DisplaySeconds.ToString(CultureInfo.InvariantCulture); notificationPosition.SelectedIndex = (int)saved.NotificationPosition;
            showHelpTab.IsChecked = saved.ShowHelpTab; showRanks.IsChecked = saved.ShowRanks;
        }
        else if (value == "appointments") autoAppointments.IsChecked = settings.Current.AutoAppointments;
        else SetInformation(settings.Current.Information);
        baseline = Values;
    }
    private void SetInformation(CrewInformation value)
    {
        information = value;
        foreach (var item in informationChoices) item.Value.IsChecked = information.Allows(item.Key);
    }
    private void RefreshConnections()
    {
        var ordered = connections.Store.ReadConnections(); ids = ordered.Select(c => c.Id).ToArray(); selection.Items.Clear();
        selection.Items.Add(new ComboBoxItem { Content = text["choose"] });
        foreach (var entry in ordered) selection.Items.Add(new ComboBoxItem { Content = entry.Name });
        selection.SelectedIndex = Array.IndexOf(ids, settings.Current.ConnectionId) + 1;
        baseline = Values;
        if (settings.Current.ConnectionId != null && selection.SelectedIndex == 0) Notice(text["error.connection_missing"], true);
    }
    internal void Toggle()
    {
        if (IsOpen) RequestClose();
        else { Home(); HideConfirmation(); ResetTabs(); revision = -1; WindowHostCommands.OpenWindowCommand.Execute(window, view);
            if (!IsOpen) throw new InvalidOperationException("The game window host did not open Chatter."); }
    }
    private void UpdateHelpTab() => Find<TabItem>("HelpTab").Visibility = settings.Current.ShowHelpTab ? Visibility.Visible : Visibility.Collapsed;
    private void ResetTabs()
    {
        UpdateHelpTab();
        var initial = Find<TabItem>(settings.Current.ShowHelpTab ? "HelpTab" : "HistoryTab");
        tabs.Reset(Find<TabControl>("Tabs").Items.IndexOf(initial));
    }
    private void RequestClose() => Navigate(() => { Home(); window.CloseWindowNow(); });
    private void OpenPortraitPicker(string? initial, Action<string?> selected)
    {
        choosingPortrait = true; portraitSelected = selected; typing.Release();
        Find<TabControl>("Tabs").Visibility = Visibility.Collapsed; Find<FrameworkElement>("PortraitPicker").Visibility = Visibility.Visible;
        portraitPicker.Open(initial);
    }
    private void ClosePortraitPicker()
    {
        portraitPicker.Close(); choosingPortrait = false; portraitSelected = null;
        Find<FrameworkElement>("PortraitPicker").Visibility = Visibility.Collapsed; Find<TabControl>("Tabs").Visibility = Visibility.Visible;
    }
    private void BackPortraitPicker() { if (portraitPicker.HasChanges) Confirm(ClosePortraitPicker); else ClosePortraitPicker(); }
    private void OpenCharacterPicker(string unitId)
    {
        if (!CanCommit()) return;
        assigningTarget = unitId; typing.Release();
        Find<TabControl>("Tabs").Visibility = Visibility.Collapsed;
        Find<FrameworkElement>("CharacterPicker").Visibility = Visibility.Visible;
        characterPicker.Open(chatter.Person(unitId)?.RegisteredCharacterId);
    }
    private void Reappoint(string unitId)
    {
        if (!CanCommit() || chatter.Paused || chatter.Busy || chatter.AppointmentRequested(unitId)) return;
        connections.ValidateSelection(settings.Current.ConnectionId);
        chatter.RequestReappointment(unitId, now()); DrawTargets(); Notice("");
    }
    private void CloseCharacterPicker()
    {
        characterPicker.Close(); assigningTarget = null;
        Find<FrameworkElement>("CharacterPicker").Visibility = Visibility.Collapsed;
        if (!choosingPortrait) Find<TabControl>("Tabs").Visibility = Visibility.Visible;
    }
    private void AssignCharacter(string characterId)
    {
        if (!CanCommit()) return;
        var character = characters.Find(characterId) ?? throw new CrewException("character_unavailable");
        var target = assigningTarget ?? throw new CrewException("person_unavailable");
        if (!character.IsComplete) connections.ValidateSelection(settings.Current.ConnectionId);
        chatter.AssignCharacter(target, characterId, now());
        CloseCharacterPicker(); revision = -1;
        Notice(character.IsComplete ? common("settings.saved") : text["appointment_pending"]);
    }
    internal void Tick()
    {
        if (!IsOpen) return;
        Run(() =>
        {
            if (revision != chatter.Version) SyncSelection();
            tabs.Pump(Dirty, () => { Home(); return true; }, action => Confirm(action));
            apiTabs.Pump(Dirty, () => { typing.Release(); CancelTest(); editor.Close(); RefreshConnections(); editor.Open(); return true; }, action => Confirm(action));
            libraryTabs.Pump(Dirty, () => { libraryView.Close(); characterView.Close(); return true; }, action => Confirm(action));
            var libraryOpen = Find<TabItem>("LibraryTab").IsSelected && assigningTarget == null;
            if (libraryOpen && libraryTabs.Current == 1 && !choosingPortrait)
            { if (!libraryView.IsOpen) libraryView.Open(); libraryView.Tick(); }
            else if (libraryView.IsOpen) libraryView.Close();
            if (libraryOpen && libraryTabs.Current == 0)
            { if (!characterView.IsOpen) characterView.Open(); characterView.Tick(); }
            else if (characterView.IsOpen) characterView.Close();
            if (revision != chatter.Version) characterPicker.RefreshSelection();
            characterPicker.Tick(); portraitPicker.Tick(); editor.Tick(); personEditor.Tick(); settingsNotice.Tick(); DrawUsage();
            Find<FrameworkElement>("ChatterStatusPane").Visibility = choosingPortrait || assigningTarget != null || Find<TabItem>("LibraryTab").IsSelected || (Find<TabItem>("SettingsTab").IsSelected && page != null) ? Visibility.Collapsed : Visibility.Visible;
            if (testing?.IsCompleted == true)
            {
                var completed = testing; testing = null; testingCancellation?.Dispose(); testingCancellation = null; editor.SetBusy(false);
                try { _ = completed.GetAwaiter().GetResult(); TestNotice(common("connection.test_succeeded")); }
                catch (OperationCanceledException) { TestNotice(common("connection.test_cancelled")); }
                catch (Exception e) { log(e); if (e is AiConnectionException ai) TestNotice(connections.Error(ai), true); else throw; }
            }
            var portraitsChanged = libraryRevision != portraits.Library.Version;
            if (portraitsChanged) { libraryRevision = portraits.Library.Version; personEditor.RefreshPortrait(); }
            if (revision == chatter.Version && !portraitsChanged) return;
            revision = chatter.Version; DrawHistory(); DrawTargets();
            Find<Button>("PauseChatter").Content = text[chatter.Paused ? "resume" : "pause"];
            if (!stopped)
            {
                if (chatter.Error != null) chatterNotice.Show(text["paused"] + "\n" + ErrorMessage(chatter.Error), true);
                else if (chatter.Paused) chatterNotice.Show(text["paused"]);
                else if (chatter.Busy) chatterNotice.Show(text["busy"]);
                else chatterNotice.Show("");
            }
        });
    }
    internal void Stop()
    {
        stopped = true; Find<TabItem>("TargetsTab").IsEnabled = false; Find<Button>("PauseChatter").IsEnabled = false;
        chatterNotice.Show(text["error.unexpected_error"], true); if (page != null) settingsNotice.Show(text["error.unexpected_error"], true);
    }
    internal string ErrorMessage(string code) => code is "connection_required" or "connection_missing" or "provider_unavailable" or "invalid_response" or "too_large" or "unexpected_error"
        ? text["error." + code] : connections.Error(new AiConnectionException(code));
    private void Saved() => settingsNotice.ShowBriefly(common("settings.saved"));
    internal void Notice(string message, bool error = false)
    {
        if (choosingPortrait && error) portraitPicker.ShowError(message);
        else if (assigningTarget != null && error) characterPicker.ShowError(message);
        else if (Find<TabItem>("LibraryTab").IsSelected && error)
        { if (libraryTabs.Current == 0) characterView.ShowError(message); else libraryView.ShowError(message); }
        else if (personEditor.IsOpen && error) personEditor.ShowError(message);
        else (page == null ? chatterNotice : settingsNotice).Show(message, error);
    }
    private void TestNotice(string message, bool error = false)
    {
        editor.Find<TextBlock>("ConnectionTestStatus").Text = error ? "" : message;
        var failure = editor.Find<TextBlock>("ConnectionTestError"); failure.Text = error ? message : "";
        failure.Visibility = error ? Visibility.Visible : Visibility.Collapsed;
    }
    private Border Card(string heading, string body, CrewPerson? person = null, FrameworkElement? action = null)
    {
        var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new Image { Source = portraits.Get(person), Stretch = Stretch.Uniform, Width = 48, Height = 48, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top });
        var words = new StackPanel(); words.Children.Add(new TextBlock { Text = heading, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap });
        words.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) }); Grid.SetColumn(words, 1); row.Children.Add(words);
        if (action != null)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            action.Margin = new Thickness(12, 0, 0, 0); action.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(action, 2); row.Children.Add(action);
        }
        return new Border { Child = row, BorderBrush = new SolidColorBrush(Color.FromArgb(255, 85, 96, 107)), BorderThickness = new Thickness(1), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 8) };
    }
    private void DrawUsage()
    {
        var usage = chatter.Usage.Snapshot;
        if (ReferenceEquals(displayedUsage, usage)) return;
        displayedUsage = usage;
        string Number(long value) => value.ToString("N0", CultureInfo.CurrentCulture);
        var summary = string.Format(text["usage.summary"], Number(usage.KnownTokens), Number(usage.ApiRequests));
        if (usage.UnknownRequests > 0) summary += string.Format(text["usage.unknown"], Number(usage.UnknownRequests));
        var latest = usage.ApiRequests == 0 ? "-" : usage.LastTokens.HasValue ? Number(usage.LastTokens.Value) + " tokens" : text["usage.unavailable"];
        Find<TextBlock>("UsageSummary").Text = summary + "\n" + string.Format(text["usage.latest"], latest);
    }
    private void DrawHistory()
    {
        var panel = Find<StackPanel>("History"); panel.Children.Clear();
        if (chatter.History.Count == 0) panel.Children.Add(new TextBlock { Text = text["empty_history"], TextWrapping = TextWrapping.Wrap });
        foreach (var remark in chatter.History.Reverse())
            panel.Children.Add(Card(remark.Target.Name + " / " + remark.Submission.Person.DisplayName(settings.Current.ShowRanks, chatter.RankBeforeName) + " / " + remark.ReceivedAt.ToString("HH:mm:ss"), remark.Submission.Text, remark.Submission.Person));
    }
    private void SyncSelection() => targetSelection.Sync(chatter.Targets, chatter.Enabled, id => chatter.Person(id) != null);
    private void DrawTargets()
    {
        SyncSelection();
        Find<Button>("AppointAll").IsEnabled = !chatter.Paused && !chatter.Busy && chatter.HasUnappointedTargets;
        var hasAppointedTargets = chatter.Targets.Any(target => chatter.Person(target.Id) != null);
        Find<Button>("AllTargetsOn").IsEnabled = hasAppointedTargets;
        Find<Button>("AllTargetsOff").IsEnabled = hasAppointedTargets;
        Find<Button>("ApplyTargets").IsEnabled = targetSelection.Dirty;
        var panel = Find<StackPanel>("Targets"); panel.Children.Clear();
        if (chatter.Targets.Count == 0) panel.Children.Add(new TextBlock { Text = text["empty_targets"], TextWrapping = TextWrapping.Wrap });
        foreach (var target in chatter.Targets)
        {
            var person = chatter.Person(target.Id);
            var label = target.Name + " / " + target.Nation + " / " + string.Format(CultureInfo.InvariantCulture, text["members"], target.Members.Count);
            if (!target.CanSpeak) label += " / " + text["leader_speaks"];
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var check = new CheckBox { IsChecked = targetSelection.Checked(target.Id), IsEnabled = person != null,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 12, 8, 0), ToolTip = text["participation"] };
            var waiting = chatter.AppointmentRequested(target.Id);
            var details = new Button { Content = text["edit_person"], IsEnabled = person != null, Height = 30, Padding = new Thickness(12, 0, 12, 0),
                HorizontalAlignment = HorizontalAlignment.Right };
            details.Click += (_, _) => Run(() => personEditor.Open(target.Id));
            var reappoint = new Button { Content = text["reappoint"], Height = 30, Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0, 0, 8, 0), IsEnabled = person != null && !waiting && !chatter.Paused && !chatter.Busy };
            reappoint.Click += (_, _) => Run(() => Reappoint(target.Id));
            var appoint = new Button { Content = text["character.appoint_from"], Height = 30, Padding = new Thickness(12, 0, 12, 0), IsEnabled = !chatter.Busy };
            appoint.Click += (_, _) => Run(() => OpenCharacterPicker(target.Id));
            var replacements = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
            replacements.Children.Add(reappoint); replacements.Children.Add(appoint);
            var actions = new StackPanel(); actions.Children.Add(details); actions.Children.Add(replacements);
            var description = person == null ? text[waiting ? "appointment_pending" : "not_appointed"] :
                person.DisplayName(settings.Current.ShowRanks, chatter.RankBeforeName) + (person.Personality.Length == 0 ? "" : "\n" + person.Personality);
            if (person != null && waiting) description += "\n" + text[chatter.Busy ? "reappointing" : "appointment_pending"];
            var card = Card(label, description, person, actions);
            Grid.SetColumn(card, 1); row.Children.Add(check); row.Children.Add(card);
            check.Click += (_, _) => Run(() =>
            {
                targetSelection.Set(target.Id, check.IsChecked == true);
                Find<Button>("ApplyTargets").IsEnabled = targetSelection.Dirty;
            });
            panel.Children.Add(row);
        }
    }
    private void Test()
    {
        if (testing != null || !CanCommit()) return;
        var draft = editor.ReadEditor(); testingCancellation = new CancellationTokenSource(); var token = testingCancellation.Token;
        typing.Release(); editor.SetBusy(true); TestNotice(common("connection.test_running"));
        testing = Task.Run(() => connections.TestAsync(draft, token), token);
    }
    private void CancelTest()
    {
        testingCancellation?.Cancel(); var old = testing; testing = null;
        var cancellation = testingCancellation; testingCancellation = null;
        if (old == null) cancellation?.Dispose();
        else _ = old.ContinueWith(t => { if (t.IsFaulted) log(t.Exception!); cancellation?.Dispose(); }, TaskScheduler.Default);
        editor.SetBusy(false); TestNotice("");
    }
    public void Dispose() { characterView.Dispose(); CloseCharacterPicker(); personEditor.Dispose(); libraryView.Dispose(); ClosePortraitPicker(); CancelTest(); typing.Release(); if (IsOpen) window.CloseWindowNow(); editor.Close(); }
    private sealed class CloseCommand : ICommand
    {
        private readonly Action action;
        internal CloseCommand(Action action) => this.action = action;
        public bool CanExecute(object parameter) => true;
        public void Execute(object parameter) => action();
        public event System.EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
