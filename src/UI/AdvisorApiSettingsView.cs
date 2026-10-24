using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Noesis;

namespace SeaPowerAIAdvisor;

// Registration forms and role selections are separate operations; no whole-settings draft is saved here.
internal sealed class AdvisorApiSettingsView
{
    private static readonly AdvisorSituationDelivery[] deliveryChoices =
        { AdvisorSituationDelivery.Upfront, AdvisorSituationDelivery.OnDemand, AdvisorSituationDelivery.OnRequest };
    private readonly ComboBox main, sub, provider, delivery;
    private readonly AdvisorApiConnectionListView cards;
    private readonly FrameworkElement deliveryPanel;
    private readonly TextBox name, timeout, outputTokens;
    private readonly StackPanel fields;
    private readonly TextBlock providerDescription, providerError;
    private readonly AdvisorServiceCatalog services = AdvisorServiceCatalog.BuiltIn;
    private IReadOnlyList<AdvisorService> providerChoices = Array.Empty<AdvisorService>();
    private readonly Dictionary<string, JObject> providerDrafts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<string>> fieldValues = new(StringComparer.Ordinal);
    private readonly Button add, duplicate, remove, saveEntry, cancelEntry, saveAssignments;
    private readonly FrameworkElement listPane, editorPane, useTab, providerDescriptionBox;
    private readonly AdvisorApiSettingsStore store;
    private readonly Func<AdvisorText> text;
    private readonly Action changed, saved, compositionNotice;
    private readonly Action<AdvisorConnectionException> error;
    private readonly Action<string, Action> confirmDelete;
    private readonly Action<Action> confirmDuplicate;
    private JObject? editorBaseline;
    private readonly TypingScope typing;
    private AdvisorSettingsDraft? data;
    private AdvisorApiConfigurationDraft? editor;
    private string[] ids = Array.Empty<string>();
    private bool updating, busy, adding;
    internal bool Editing => editor != null;
    internal string TestName => name.Text ?? "";
    private string? MainId => main.SelectedIndex <= 0 ? null : ids[main.SelectedIndex - 1];
    private string? SubId => sub.SelectedIndex <= 0 ? null : ids[sub.SelectedIndex - 1];
    private bool EditorChanged => editor != null && !JToken.DeepEquals(editorBaseline, EditorValues());
    internal bool HasChanges => EditorChanged || (data != null && (MainId != data.MainId || SubId != data.SubId));

    internal AdvisorApiSettingsView(FrameworkElement content, AdvisorApiSettingsStore store, Func<AdvisorText> text,
        Action<Action> guard, TypingScope typing, Action changed, Action saved, Action compositionNotice, Action<AdvisorConnectionException> error, Action<string, Action> confirmDelete, Action<Action> confirmDuplicate)
    {
        this.store = store; this.text = text; this.typing = typing; this.changed = changed;
        this.saved = saved; this.compositionNotice = compositionNotice; this.error = error; this.confirmDelete = confirmDelete; this.confirmDuplicate = confirmDuplicate;
        T Named<T>(string id) where T : class => content.FindName(id) as T ?? throw new InvalidOperationException("Missing API control: " + id);
        main = Named<ComboBox>("MainApiConfiguration"); sub = Named<ComboBox>("SubApiConfiguration");
        listPane = Named<FrameworkElement>("ApiConnectionList");
        cards = new AdvisorApiConnectionListView(Named<StackPanel>("ApiConnectionCards"), services, text, id => Run(() => Edit(id)), guard);
        name = Named<TextBox>("ApiConfigurationName"); timeout = Named<TextBox>("RequestTimeout"); provider = Named<ComboBox>("ApiProvider");
        outputTokens = Named<TextBox>("OutputTokens");
        delivery = Named<ComboBox>("SituationDelivery"); deliveryPanel = Named<FrameworkElement>("SituationDeliveryPanel");
        delivery.SelectionChanged += (_, _) => guard(NotifyChanged);
        fields = Named<StackPanel>("ApiProviderFields"); providerError = Named<TextBlock>("ApiProviderError");
        providerDescription = Named<TextBlock>("ApiProviderDescription"); providerDescriptionBox = Named<Border>("ApiProviderDescriptionBox");
        add = Named<Button>("AddApiConfiguration"); duplicate = Named<Button>("DuplicateApiConfiguration"); remove = Named<Button>("RemoveApiConfiguration");
        saveEntry = Named<Button>("SaveApiEntry"); cancelEntry = Named<Button>("CancelApiEntry"); saveAssignments = Named<Button>("SaveApiAssignments");
        editorPane = Named<FrameworkElement>("ApiEditor"); useTab = Named<FrameworkElement>("ApiUseTab");
        void Bind(Button button, Action action) => button.Click += (_, _) => guard(() => Run(action));
        Bind(add, Add); Bind(duplicate, Duplicate); Bind(remove, Remove); Bind(saveEntry, SaveEntry); Bind(cancelEntry, CancelEntry); Bind(saveAssignments, SaveAssignments);
        name.TextChanged += (_, _) => guard(NotifyChanged);
        timeout.TextChanged += (_, _) => guard(NotifyChanged);
        outputTokens.TextChanged += (_, _) => guard(NotifyChanged);
        outputTokens.GotKeyboardFocus += (_, _) => guard(() => typing.Acquire(outputTokens)); outputTokens.LostKeyboardFocus += (_, _) => guard(() => typing.Release(outputTokens));
        timeout.GotKeyboardFocus += (_, _) => guard(() => typing.Acquire(timeout)); timeout.LostKeyboardFocus += (_, _) => guard(() => typing.Release(timeout));
        name.GotKeyboardFocus += (_, _) => guard(() => typing.Acquire(name)); name.LostKeyboardFocus += (_, _) => guard(() => typing.Release(name));
        provider.SelectionChanged += (_, _) => guard(() =>
        {
            if (updating || editor == null || provider.SelectedIndex < 0 || provider.SelectedIndex >= providerChoices.Count) return;
            ReadEditor(false); providerDrafts[editor.Provider] = (JObject)editor.Settings.DeepClone();
            var selected = providerChoices[provider.SelectedIndex]; editor.Provider = selected.Id;
            editor.Settings = providerDrafts.TryGetValue(selected.Id, out var previous) ? (JObject)previous.DeepClone() : selected.Defaults();
            ShowFields(guard); changed();
        });
        renderFields = () => ShowFields(guard);
    }
    private void Run(Action action) { try { action(); } catch (AdvisorConnectionException failure) { error(failure); } }
    internal void Open() { editor = null; Refresh(store.Read(), false); }
    internal void Close() { editor = null; Refresh(null, false); }
    internal void Focus() { if (main.IsEnabled) main.Focus(); }
    internal AdvisorApiConfiguration TestConfiguration()
    { ReadEditor(); return (editor ?? throw new AdvisorConnectionException("api_selection_invalid")).Build(); }
    internal AdvisorConnectionOptions TestOptions() => store.TestOptions();
    private readonly Action renderFields;
    private void ReadEditor(bool includeLimits = true)
    {
        if (editor == null) return;
        editor.Name = name.Text ?? "";
        if (delivery.SelectedIndex >= 0) editor.SituationDelivery = deliveryChoices[delivery.SelectedIndex];
        if (includeLimits)
        {
            editor.RequestTimeoutSeconds = AdvisorRequestTimeout.Parse(timeout.Text ?? "");
            editor.MaxOutputTokens = AdvisorOutputTokens.Parse(outputTokens.Text ?? "");
        }
        foreach (var field in fieldValues) editor.Settings[field.Key] = field.Value().Trim();
    }
    // Compare raw editable values so invalid/incomplete input can still be discarded without validation.
    private JObject EditorValues() => new()
    {
        ["name"] = name.Text ?? "", ["provider"] = editor!.Provider, ["timeout"] = timeout.Text ?? "",
        ["output_tokens"] = outputTokens.Text ?? "", ["delivery"] = delivery.SelectedIndex,
        ["fields"] = new JObject(fieldValues.Select(field => new JProperty(field.Key, field.Value())))
    };
    private void ShowFields(Action<Action> guard)
    {
        typing.Release(); fields.Children.Clear(); fieldValues.Clear();
        var service = editor == null ? null : services.Find(editor.Provider);
        providerDescription.Text = service == null ? "" : service.Description(text());
        providerDescriptionBox.Visibility = string.IsNullOrWhiteSpace(providerDescription.Text) ? Visibility.Collapsed : Visibility.Visible;
        providerError.Text = service == null && editor != null ? text()["connection.provider_unavailable"] : "";
        providerError.Visibility = providerError.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        deliveryPanel.Visibility = service?.Independent == false ? Visibility.Visible : Visibility.Collapsed;
        if (service?.Independent == true) editor!.SituationDelivery = AdvisorSituationDelivery.OnDemand;
        var wasUpdating = updating; updating = true;
        try
        {
            delivery.Items.Clear();
            foreach (var choice in deliveryChoices)
                delivery.Items.Add(new ComboBoxItem { Content = text()["connection.situation_" + choice.Code()] });
            delivery.SelectedIndex = Array.IndexOf(deliveryChoices, editor?.SituationDelivery ?? AdvisorSituationDelivery.OnDemand);
        }
        finally { updating = wasUpdating; }
        if (service == null) return;
        foreach (var field in service.Fields)
        {
            fields.Children.Add(new TextBlock { Text = field.Caption(text()), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 4) });
            var value = (string?)editor!.Settings[field.Key] ?? field.DefaultValue;
            if (field.Secret)
            {
                var input = new PasswordBox { FontSize = 14, Padding = new Thickness(7), MaxLength = 512, ShowLastCharacterDuration = 0,
                    Background = (Brush)fields.FindResource("Brush.TextBox.Background"), CaretBrush = (Brush)fields.FindResource("Brush.TextBox.ForegroundFocus"), Password = value };
                input.PasswordChanged += (_, _) => guard(NotifyChanged);
                input.GotKeyboardFocus += (_, _) => guard(() => typing.Acquire(input)); input.LostKeyboardFocus += (_, _) => guard(() => typing.Release(input));
                fieldValues.Add(field.Key, () => input.Password); fields.Children.Add(input);
            }
            else
            {
                var input = new TextBox { FontSize = 14, Padding = new Thickness(7), MaxLength = 2048, Text = value };
                input.TextChanged += (_, _) => guard(NotifyChanged);
                input.GotKeyboardFocus += (_, _) => guard(() => typing.Acquire(input)); input.LostKeyboardFocus += (_, _) => guard(() => typing.Release(input));
                fieldValues.Add(field.Key, () => input.Text ?? ""); fields.Children.Add(input);
            }
        }
    }
    internal void SetBusy(bool value)
    {
        busy = value;
        main.IsEnabled = sub.IsEnabled = saveAssignments.IsEnabled = useTab.IsEnabled = data != null && !busy && !Editing;
        listPane.IsEnabled = add.IsEnabled = data != null && !busy && !Editing;
        duplicate.IsEnabled = remove.IsEnabled = Editing && !adding && !busy;
        name.IsEnabled = timeout.IsEnabled = outputTokens.IsEnabled = provider.IsEnabled = delivery.IsEnabled = fields.IsEnabled = saveEntry.IsEnabled = cancelEntry.IsEnabled = Editing && !busy;
    }
    private void Refresh(AdvisorSettingsDraft? value, bool keepAssignments)
    {
        var selectedMain = keepAssignments ? MainId : value?.MainId;
        var selectedSub = keepAssignments ? SubId : value?.SubId;
        updating = true;
        try
        {
            data = value; ids = value?.Configurations.Select(c => c.Id).ToArray() ?? Array.Empty<string>();
            main.Items.Clear(); sub.Items.Clear();
            main.Items.Add(new ComboBoxItem { Content = text()["connection.choose_configuration"] });
            sub.Items.Add(new ComboBoxItem { Content = text()["connection.no_sub"] });
            foreach (var id in ids)
                foreach (var control in new[] { main, sub }) control.Items.Add(new ComboBoxItem { Content = value!.Find(id)!.Name });
            main.SelectedIndex = Array.IndexOf(ids, selectedMain) + 1; sub.SelectedIndex = Array.IndexOf(ids, selectedSub) + 1;
            cards.Show(value?.Configurations.Select(c => c.Build()).ToArray() ?? Array.Empty<AdvisorApiConfiguration>(), value?.MainId, value?.SubId);
            typing.Release(); editorBaseline = null; listPane.Visibility = Visibility.Visible;
            editorPane.Visibility = Visibility.Collapsed; name.Text = timeout.Text = outputTokens.Text = ""; provider.Items.Clear(); fields.Children.Clear(); fieldValues.Clear(); providerDrafts.Clear();
            providerDescription.Text = ""; providerDescriptionBox.Visibility = Visibility.Collapsed;
        }
        finally { updating = false; }
        SetBusy(busy);
    }
    private void NotifyChanged() { if (!updating) changed(); }
    private void ShowEditor(AdvisorApiConfigurationDraft value, bool isNew)
    {
        editor = value; adding = isNew; updating = true;
        try
        {
            name.Text = value.Name; timeout.Text = value.RequestTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            outputTokens.Text = value.MaxOutputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture); providerDrafts.Clear(); provider.Items.Clear();
            providerChoices = services.Services;
            foreach (var service in providerChoices) provider.Items.Add(new ComboBoxItem { Content = service.Caption(text()) });
            var index = providerChoices.ToList().FindIndex(s => s.Id == value.Provider);
            if (index < 0) { provider.Items.Add(new ComboBoxItem { Content = value.Provider }); index = providerChoices.Count; }
            provider.SelectedIndex = index; renderFields();
        }
        finally { updating = false; }
        listPane.Visibility = Visibility.Collapsed; editorPane.Visibility = Visibility.Visible;
        editorBaseline = EditorValues(); saveEntry.Content = text()[adding ? "settings.register" : "settings.save_form"];
        SetBusy(false); changed(); typing.Release(); name.Focus();
    }
    private void Add() { if (data != null && !Editing) ShowEditor(store.Read().Add(text()["connection.configuration_name_pattern"]), true); }
    private void Edit(string id)
    {
        if (Editing || busy) return;
        var entry = store.Read().Find(id) ?? throw new AdvisorConnectionException("api_selection_invalid");
        ShowEditor(new AdvisorApiConfigurationDraft(entry.Build()), false);
    }
    private void Duplicate()
    {
        if (editor == null || adding || busy) return;
        if (QuestionInputController.IsComposing) { compositionNotice(); return; }
        var id = editor.Id;
        void Copy() => Run(() =>
        {
            var result = store.Duplicate(id, text()["connection.configuration_copy_suffix"]);
            editor = null; Refresh(result, true); changed(); saved(); add.Focus();
        });
        if (EditorChanged) confirmDuplicate(Copy); else Copy();
    }
    private void CancelEntry()
    { var result = store.Read(); editor = null; Refresh(result, true); changed(); add.Focus(); }
    private void SaveEntry()
    {
        if (QuestionInputController.IsComposing) { compositionNotice(); return; }
        ReadEditor(); var entry = editor ?? throw new AdvisorConnectionException("api_selection_invalid");
        var result = store.SaveEntry(entry, adding); editor = null; Refresh(result, true); changed(); saved(); add.Focus();
    }
    private void SaveAssignments()
    { var result = store.Assign(MainId, SubId); Refresh(result, false); changed(); saved(); }
    private void Remove()
    {
        if (editor == null || adding || busy) return;
        var id = editor.Id; var caption = data!.Find(id)!.Name;
        confirmDelete(caption, () => Run(() =>
        { var result = store.Remove(id); editor = null; Refresh(result, true); changed(); saved(); add.Focus(); }));
    }
}
