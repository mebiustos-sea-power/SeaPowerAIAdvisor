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
    private readonly SettingsTabNavigation tabs, apiTabs;
    private readonly ComboBox selection, notificationPosition;
    private readonly TextBox interval, duration, exchangeChance;
    private readonly CheckBox showHelpTab;
    private readonly ImageSource portrait;
    private readonly UiNotice settingsNotice, chatterNotice;
    private readonly CrewTargetSelection targetSelection = new();
    private readonly Dictionary<string, CheckBox> informationChoices = new(StringComparer.Ordinal);
    private CrewInformation information = CrewInformation.Default;
    private string[] ids = Array.Empty<string>();
    private string? page, baseline;
    private bool changing, stopped;
    private int revision = -1;
    private CrewUsageSnapshot? displayedUsage;
    private Action? confirmed;
    private CancellationTokenSource? testingCancellation;
    private Task<string>? testing;
    internal bool IsOpen => host.Windows.Contains(window);
    private T Find<T>(string name) where T : class => root.FindName(name) as T ?? throw new InvalidOperationException("Missing Chatter control: " + name);
    private string? SelectedId => selection.SelectedIndex <= 0 ? null : ids[selection.SelectedIndex - 1];
    private string Values => page switch
    {
        "chatter" => interval.Text + ":" + duration.Text + ":" + notificationPosition.SelectedIndex + ":" + showHelpTab.IsChecked + ":" + exchangeChance.Text,
        "information" => information.Key,
        _ => SelectedId ?? ""
    };
    private bool Dirty => targetSelection.Dirty || (page == "api" && apiTabs.Current == 1 ? editor.HasChanges : page != null && Values != baseline);

    internal CrewWindow(FrameworkElement view, WindowHost host, CrewChatter chatter, CrewSettingsStore settings, ICrewConnections connections,
        Func<string, string> common, CrewText text, ImageSource portrait, Func<double> now, Action<Action> guard, Action<Exception> log)
    {
        this.view = view; this.host = host; this.chatter = chatter; this.settings = settings; this.connections = connections;
        this.common = common; this.text = text; this.portrait = portrait; this.now = now; this.guard = guard; this.log = log;
        using var source = typeof(CrewWindow).Assembly.GetManifestResourceStream("SeaPowerAIAdvisor.CrewWindow.xaml") ?? throw new FileNotFoundException("The Chatter view is missing.");
        using var reader = new StreamReader(source);
        root = GUI.ParseXaml(reader.ReadToEnd()) as FrameworkElement ?? throw new InvalidOperationException("The Chatter view is invalid.");
        foreach (var item in text.Values) root.Resources["Crew." + item.Key] = item.Value;
        root.Resources["Crew.save"] = common("settings.save_form");
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
        interval = Find<TextBox>("Interval"); duration = Find<TextBox>("Duration"); exchangeChance = Find<TextBox>("ExchangeChance"); showHelpTab = Find<CheckBox>("ShowHelpTab");
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
        tabs = Navigation("Tabs"); apiTabs = Navigation("ApiTabs");
        Bind("OpenConnections", () => OpenPage("api")); Bind("OpenChatter", () => OpenPage("chatter"));
        Bind("OpenInformation", () => OpenPage("information"));
        Bind("ResetInformation", () => { SetInformation(CrewInformation.Default); settingsNotice.Show(""); });
        Bind("Back", () => Navigate(Home));
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
            if (chatter.Paused && chatter.HasEnabledTargets) connections.ValidateSelection(settings.Current.ConnectionId);
            chatter.SetPaused(!chatter.Paused, now());
        });
        Bind("SaveConnection", () =>
        {
            if (!CanCommit()) return;
            var saved = settings.Current; settings.Save(new CrewSettings(SelectedId, saved.IntervalMinutes, saved.DisplaySeconds, saved.NotificationPosition, saved.ShowHelpTab, saved.ExchangeChancePercent, saved.Information));
            baseline = Values; editor.UpdateCards(); Saved();
        });
        Bind("SaveChatter", () =>
        {
            if (!CanCommit()) return;
            int Read(TextBox input) => int.TryParse(input.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : throw new CrewException("settings_invalid");
            settings.Save(new CrewSettings(settings.Current.ConnectionId, Read(interval), Read(duration), (CrewNotificationPosition)notificationPosition.SelectedIndex, showHelpTab.IsChecked == true, Read(exchangeChance), settings.Current.Information));
            UpdateHelpTab();
            baseline = Values; Saved();
        });
        Bind("SaveInformation", () =>
        {
            if (!CanCommit()) return;
            var saved = settings.Current;
            settings.Save(new CrewSettings(saved.ConnectionId, saved.IntervalMinutes, saved.DisplaySeconds, saved.NotificationPosition, saved.ShowHelpTab, saved.ExchangeChancePercent, information));
            baseline = Values; Saved();
        });
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
        window.OnClosed += (_, _) => { typing.Release(); CancelTest(); };
        Home(); ResetTabs(); apiTabs.Reset(); DrawUsage();
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
        Find<FrameworkElement>("Confirmation").Visibility = Visibility.Visible;
        Find<Button>("CancelConfirmation").Focus();
    }
    private void HideConfirmation() { confirmed = null; Find<FrameworkElement>("Confirmation").Visibility = Visibility.Collapsed; Find<TabControl>("Tabs").IsEnabled = true; }
    private void Navigate(Action action) { if (Dirty) Confirm(action); else action(); }
    private void Home()
    {
        typing.Release(); CancelTest(); editor.Close(); targetSelection.Discard(); revision = -1; page = null; baseline = null; settingsNotice.Show("");
        Find<FrameworkElement>("SettingsHome").Visibility = Visibility.Visible;
        Find<FrameworkElement>("SettingsPage").Visibility = Visibility.Collapsed;
    }
    private void OpenPage(string value)
    {
        page = value; typing.Release(); settingsNotice.Show("");
        Find<TextBlock>("SettingsCategoryTitle").Text = text[value switch { "api" => "connections", "chatter" => "chatter", "information" => "information", _ => throw new ArgumentOutOfRangeException(nameof(value)) }];
        Find<FrameworkElement>("SettingsHome").Visibility = Visibility.Collapsed; Find<FrameworkElement>("SettingsPage").Visibility = Visibility.Visible;
        Find<FrameworkElement>("ApiTabs").Visibility = value == "api" ? Visibility.Visible : Visibility.Collapsed;
        Find<FrameworkElement>("ChatterSettings").Visibility = value == "chatter" ? Visibility.Visible : Visibility.Collapsed;
        Find<FrameworkElement>("InformationSettings").Visibility = value == "information" ? Visibility.Visible : Visibility.Collapsed;
        if (value == "api") { apiTabs.Reset(); RefreshConnections(); editor.Open(); }
        else if (value == "chatter")
        {
            var saved = settings.Current;
            interval.Text = saved.IntervalMinutes.ToString(CultureInfo.InvariantCulture);
            exchangeChance.Text = saved.ExchangeChancePercent.ToString(CultureInfo.InvariantCulture);
            duration.Text = saved.DisplaySeconds.ToString(CultureInfo.InvariantCulture); notificationPosition.SelectedIndex = (int)saved.NotificationPosition;
            showHelpTab.IsChecked = saved.ShowHelpTab;
        }
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
    internal void Tick()
    {
        if (!IsOpen) return;
        Run(() =>
        {
            if (revision != chatter.Version) SyncSelection();
            tabs.Pump(Dirty, () => { Home(); return true; }, action => Confirm(action));
            apiTabs.Pump(Dirty, () => { typing.Release(); CancelTest(); editor.Close(); RefreshConnections(); editor.Open(); return true; }, action => Confirm(action));
            editor.Tick(); settingsNotice.Tick(); DrawUsage();
            Find<FrameworkElement>("ChatterStatusPane").Visibility = Find<TabItem>("SettingsTab").IsSelected && page != null ? Visibility.Collapsed : Visibility.Visible;
            if (testing?.IsCompleted == true)
            {
                var completed = testing; testing = null; testingCancellation?.Dispose(); testingCancellation = null; editor.SetBusy(false);
                try { _ = completed.GetAwaiter().GetResult(); TestNotice(common("connection.test_succeeded")); }
                catch (OperationCanceledException) { TestNotice(common("connection.test_cancelled")); }
                catch (Exception e) { log(e); if (e is AiConnectionException ai) TestNotice(connections.Error(ai), true); else throw; }
            }
            if (revision == chatter.Version) return;
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
    internal void Notice(string message, bool error = false) => (page == null ? chatterNotice : settingsNotice).Show(message, error);
    private void TestNotice(string message, bool error = false)
    {
        editor.Find<TextBlock>("ConnectionTestStatus").Text = error ? "" : message;
        var failure = editor.Find<TextBlock>("ConnectionTestError"); failure.Text = error ? message : "";
        failure.Visibility = error ? Visibility.Visible : Visibility.Collapsed;
    }
    private Border Card(string heading, string body)
    {
        var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new Image { Source = portrait, Width = 48, Height = 48, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top });
        var words = new StackPanel(); words.Children.Add(new TextBlock { Text = heading, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap });
        words.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) }); Grid.SetColumn(words, 1); row.Children.Add(words);
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
            panel.Children.Add(Card(remark.Target.Name + " / " + remark.Submission.Person.Name + " / " + remark.ReceivedAt.ToString("HH:mm:ss"), remark.Submission.Text));
    }
    private void SyncSelection() => targetSelection.Sync(chatter.Targets, chatter.Enabled);
    private void DrawTargets()
    {
        SyncSelection();
        Find<Button>("ApplyTargets").IsEnabled = targetSelection.Dirty;
        var panel = Find<StackPanel>("Targets"); panel.Children.Clear();
        if (chatter.Targets.Count == 0) panel.Children.Add(new TextBlock { Text = text["empty_targets"], TextWrapping = TextWrapping.Wrap });
        foreach (var target in chatter.Targets)
        {
            var person = chatter.Person(target.Id);
            var label = target.Name + " / " + target.Nation + " / " + string.Format(CultureInfo.InvariantCulture, text["members"], target.Members.Count);
            if (!target.CanSpeak) label += " / " + text["leader_speaks"];
            var check = new CheckBox { Content = Card(label, person == null ? text[chatter.Enabled(target.Id) ? "appointment_pending" : "not_appointed"] : person.Name + " / " + person.Personality),
                IsChecked = targetSelection.Checked(target.Id), HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 8) };
            check.Click += (_, _) => Run(() =>
            {
                targetSelection.Set(target.Id, check.IsChecked == true);
                Find<Button>("ApplyTargets").IsEnabled = targetSelection.Dirty;
            });
            panel.Children.Add(check);
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
    public void Dispose() { CancelTest(); typing.Release(); if (IsOpen) window.CloseWindowNow(); editor.Close(); }
    private sealed class CloseCommand : ICommand
    {
        private readonly Action action;
        internal CloseCommand(Action action) => this.action = action;
        public bool CanExecute(object parameter) => true;
        public void Execute(object parameter) => action();
        public event System.EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
