using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Noesis;
using SeaPowerAIAdvisor.Shared;
using SeaPowerAIMissionCreator.Connections;
using SeaPowerAIMissionCreator.Development;

namespace SeaPowerAIMissionCreator.UI;

// This controller composes Creator selection and developer preferences around the common connection editor.
internal sealed class MissionCreatorConnectionView : IDisposable
{
    internal Grid Root { get; } = new() { Margin = new Thickness(12) };
    private readonly MissionCreatorConnections service;
    private readonly MissionCreatorLocalApiDriver driver;
    private readonly MissionCreatorText text;
    private readonly Action<Exception> report;
    private readonly TypingScope typing = new();
    private readonly ComboBox selection = new() { MinWidth = 180, MinHeight = 30 };
    private readonly Button settings, saveSelection;
    private readonly TextBlock heading;
    private readonly StackPanel selectionPane = new() { Margin = new Thickness(0, 0, 0, 12) };
    private readonly Grid pane = new();
    private readonly TabControl tabs = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly CheckBox developer;
    private readonly TextBox port = new() { Width = 140, HorizontalAlignment = HorizontalAlignment.Left, FontSize = 14, Padding = new Thickness(7), MaxLength = 5 };
    private readonly TextBlock apiStatus = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock notice = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Border confirmation;
    private readonly TextBlock confirmText = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
    private readonly AiConnectionsView connections;
    private readonly SettingsTabNavigation navigation;
    private readonly Button test;
    private readonly TextBlock testStatus, testError;
    private Action? confirmAction;
    private string[] ids = Array.Empty<string>();
    private bool updating, showSettings;
    private string? observedRun;

    internal MissionCreatorConnectionView(MissionCreatorConnections service, MissionCreatorLocalApiDriver driver, MissionCreatorText text, Action<Exception> report)
    {
        this.service = service; this.driver = driver; this.text = text; this.report = report;
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var top = new Grid();
        heading = new TextBlock { Text = text["settings"], FontSize = 14, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        top.Children.Add(heading); settings = Button(text["settings"], ToggleSettings); settings.HorizontalAlignment = HorizontalAlignment.Right;
        top.Children.Add(settings); Root.Children.Add(top);
        Grid.SetRow(pane, 1); pane.Margin = new Thickness(0, 12, 0, 0); Root.Children.Add(pane); pane.Visibility = Visibility.Collapsed; pane.Children.Add(tabs);
        Grid.SetRow(notice, 2); Root.Children.Add(notice);
        var form = AiConnectionsView.Create(key => text[key]);
        connections = new AiConnectionsView(form, service.Store, () => AiProviderDefinition.BuiltIn, key => text[key], () => text.Language,
            typing, Run, CanCommit, (name, action) => Confirm(text.Format("confirm_delete", name), action),
            action => Confirm(text["confirm_copy"], action), error => notice.Text = text.Error(error.Code), role: id => id == service.Store.Current.SelectedId ? text["selected"] : null);

        selectionPane.Children.Add(new TextBlock { Text = text["select_connection"], FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
        var selectRow = new Grid(); selectRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        selectRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); selectRow.Children.Add(selection);
        saveSelection = Button(text["save"], SaveSelection); Grid.SetColumn(saveSelection, 1); selectRow.Children.Add(saveSelection); selectionPane.Children.Add(selectRow);
        var registration = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        registration.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); registration.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        registration.Children.Add(selectionPane); Grid.SetRow(form, 1); registration.Children.Add(form);
        tabs.Items.Add(new TabItem { Header = text["connections"], Content = registration });
        connections.BeforeEdit = action =>
        {
            if (SelectionDirty) Confirm(text["confirm_discard"], () => { RefreshSelection(false); action(); }); else action();
        };
        var development = new StackPanel { Margin = new Thickness(0, 12, 8, 0) }; developer = new CheckBox { Content = text["developer_enabled"], FontSize = 13 };
        development.Children.Add(developer); development.Children.Add(new TextBlock { Text = text["developer_port"], FontSize = 12, Margin = new Thickness(0, 12, 0, 4) }); development.Children.Add(port);
        var saveDeveloper = Button(text["save"], () => { if (!CanCommit()) return; if (!int.TryParse(port.Text, out var value)) throw new AiConnectionException("developer_port_invalid"); service.Store.SaveDeveloper(developer.IsChecked == true, value); notice.Text = text["saved"]; });
        saveDeveloper.HorizontalAlignment = HorizontalAlignment.Right; saveDeveloper.Margin = new Thickness(0, 10, 0, 0); development.Children.Add(saveDeveloper); development.Children.Add(apiStatus);
        tabs.Items.Add(new TabItem { Header = text["developer"], Content = development });
        port.GotKeyboardFocus += (_, _) => Run(() => typing.Acquire(port)); port.LostKeyboardFocus += (_, _) => Run(() => typing.Release(port));
        navigation = new SettingsTabNavigation(index => tabs.SelectedIndex = index, () => typing.Release());
        tabs.SelectionChanged += (_, args) => { if (ReferenceEquals(args.Source, tabs)) navigation.Request(tabs.SelectedIndex); };
        test = connections.Find<Button>("TestConnection"); testStatus = connections.Find<TextBlock>("ConnectionTestStatus"); testError = connections.Find<TextBlock>("ConnectionTestError");
        connections.Changed += () => { selectionPane.Visibility = connections.Editing ? Visibility.Collapsed : Visibility.Visible; notice.Text = ""; observedRun = null; testStatus.Text = testError.Text = ""; testError.Visibility = Visibility.Collapsed; UpdateTest(); }; connections.Saved += () => notice.Text = text["saved"];
        test.Click += (_, _) => Run(() =>
        {
            if (service.Last?.Completion.IsCompleted == false) { service.CancelTest(); return; }
            if (!CanCommit()) return; testError.Visibility = Visibility.Collapsed;
            var run = service.StartTest(connections.ReadEditor().Build()); observedRun = run.Id; UpdateTest();
        });
        var confirmContent = new StackPanel(); confirmContent.Children.Add(confirmText); var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(Button(text["keep_editing"], () => { confirmAction = null; HideConfirmation(); }));
        buttons.Children.Add(Button(text["confirm"], () => { var action = confirmAction; confirmAction = null; HideConfirmation(); action?.Invoke(); })); confirmContent.Children.Add(buttons);
        confirmation = new Border { Background = new SolidColorBrush(Color.FromArgb(255, 28, 40, 52)), BorderBrush = new SolidColorBrush(Color.FromArgb(255, 96, 112, 128)), BorderThickness = new Thickness(1), Padding = new Thickness(16),
            Margin = new Thickness(20), VerticalAlignment = VerticalAlignment.Center, Child = confirmContent, Visibility = Visibility.Collapsed };
        Grid.SetRowSpan(confirmation, 3); Root.Children.Add(confirmation);
        selection.SelectionChanged += (_, _) => Run(() => { if (!updating) notice.Text = ""; });
        service.Store.Changed += RefreshSelection; driver.Tick += Tick; RefreshSelection(false); ResetDeveloper();
    }
    private bool DeveloperDirty => developer.IsChecked != service.Store.Current.DeveloperApiEnabled || port.Text != service.Store.Current.DeveloperApiPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private string? PendingSelection => selection.SelectedIndex <= 0 ? null : ids[selection.SelectedIndex - 1];
    private bool SelectionDirty => PendingSelection != service.Store.Current.SelectedId;
    private bool Dirty => navigation.Current == 0 ? SelectionDirty || connections.HasChanges : DeveloperDirty;
    private void ToggleSettings()
    {
        void Toggle() { typing.Release(); showSettings = !showSettings; pane.Visibility = showSettings ? Visibility.Visible : Visibility.Collapsed;
            settings.Content = text[showSettings ? "back" : "settings"]; heading.Visibility = showSettings ? Visibility.Visible : Visibility.Collapsed;
            if (showSettings) { ResetConnections(); ResetDeveloper(); navigation.Reset(); } else { connections.Close(); RefreshSelection(false); }
            notice.Text = ""; }
        if (showSettings && Dirty) Confirm(text["confirm_discard"], Toggle); else Toggle();
    }
    private void ResetDeveloper() { developer.IsChecked = service.Store.Current.DeveloperApiEnabled; port.Text = service.Store.Current.DeveloperApiPort.ToString(System.Globalization.CultureInfo.InvariantCulture); }
    private void SaveSelection()
    {
        if (!CanCommit()) return;
        service.Store.Select(PendingSelection); connections.UpdateCards(); notice.Text = text["saved"];
    }
    private void ResetConnections() { connections.Open(); selectionPane.Visibility = Visibility.Visible; RefreshSelection(false); }
    private void RefreshSelection() => RefreshSelection(true);
    private void RefreshSelection(bool keepPending)
    {
        var selected = keepPending ? PendingSelection : service.Store.Current.SelectedId;
        updating = true;
        try { var values = service.Store.ReadConnections(); ids = values.Select(c => c.Id).ToArray(); selection.Items.Clear(); selection.Items.Add(new ComboBoxItem { Content = text["choose_connection"] });
            foreach (var entry in values) selection.Items.Add(new ComboBoxItem { Content = entry.Name }); selection.SelectedIndex = Array.IndexOf(ids, selected) + 1; }
        finally { updating = false; }
    }
    private bool CanCommit() { if (!GameTextInput.IsComposing) return true; notice.Text = text["ime"]; return false; }
    private Button Button(string label, Action action)
    { var button = new Button { Content = label, MinHeight = 30, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(8, 0, 0, 0) }; button.Click += (_, _) => Run(action); return button; }
    private void Confirm(string message, Action action) { typing.Release(); confirmText.Text = message; confirmAction = action; confirmation.Visibility = Visibility.Visible; pane.IsEnabled = settings.IsEnabled = false; }
    private void HideConfirmation() { confirmation.Visibility = Visibility.Collapsed; pane.IsEnabled = settings.IsEnabled = true; }
    private void Tick() => Run(() =>
    {
        connections.Tick(); if (showSettings && navigation.HasPending) navigation.Pump(Dirty, () => { ResetConnections(); ResetDeveloper(); return true; }, action => Confirm(text["confirm_discard"], action));
        apiStatus.Text = driver.Status; UpdateTest();
    });
    private void UpdateTest()
    {
        var run = service.Last; var busy = run?.Completion.IsCompleted == false; connections.SetBusy(busy); selectionPane.IsEnabled = !busy; test.IsEnabled = busy || connections.Editing;
        test.Content = text[busy ? "cancel" : "connection.test"];
        if (run == null || run.Id != observedRun) return;
        var status = run.Status(); var state = (string)status["state"]!;
        testStatus.Text = state is "running" or "completed" or "cancelled" ? text["test_" + state] : "";
        testError.Text = state == "failed" ? text.Error((string)status["error"]!) : ""; testError.Visibility = state == "failed" ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Run(Action action)
    {
        try { action(); }
        catch (AiConnectionException error) { notice.Text = text.Error(error.Code); }
        catch (Exception error) { notice.Text = text.Format("error", "unexpected_error"); report(error); }
    }
    internal void Hidden() { typing.Release(); connections.Close(); showSettings = false; pane.Visibility = Visibility.Collapsed; settings.Content = text["settings"]; heading.Visibility = Visibility.Collapsed; RefreshSelection(false); confirmAction = null; HideConfirmation(); }
    public void Dispose() { driver.Tick -= Tick; service.Store.Changed -= RefreshSelection; typing.Release(); connections.Close(); }
}
