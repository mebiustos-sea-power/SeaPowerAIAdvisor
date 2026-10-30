using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Noesis;

namespace SeaPowerAIAdvisor.Shared;

// Optional feature controls participate in the same dirty check and save as the common form.
internal interface IAiConnectionEditorOptions
{
    void Show(AiConnectionDraft entry, IAiConnectionProvider? provider);
    void Read(AiConnectionDraft entry);
    JToken Values();
    void SetEnabled(bool enabled);
}

internal sealed class AiConnectionsView
{
    internal FrameworkElement Root { get; }
    internal T Find<T>(string id) where T : class => Root.FindName(id) as T ?? throw new InvalidOperationException("Missing connection control: " + id);
    private readonly IAiConnectionStore store;
    private readonly Func<IReadOnlyList<IAiConnectionProvider>> providers;
    private readonly Func<string, string> text;
    private readonly Func<string> language;
    private readonly TypingScope typing;
    private readonly DeferredInputFocus focus;
    private readonly Action<Action> guard;
    private readonly Func<bool> canCommit;
    private readonly Action<string, Action> confirmDelete;
    private readonly Action<Action> confirmDuplicate;
    private readonly Action<AiConnectionException> error;
    private readonly IAiConnectionEditorOptions? options;
    private readonly Func<string, string?> role;
    private readonly AiConnectionListView cards;
    private readonly TextBox name, timeout, outputTokens, prompt;
    private readonly ComboBox provider;
    private readonly StackPanel fields;
    private readonly TextBlock description, providerError;
    private readonly FrameworkElement descriptionBox, listPane, editorPane;
    private readonly Button add, duplicate, remove, save, cancel;
    private readonly Dictionary<string, JObject> providerDrafts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<string>> values = new(StringComparer.Ordinal);
    private IReadOnlyList<IAiConnectionProvider> choices = Array.Empty<IAiConnectionProvider>();
    private JObject? baseline;
    private AiConnectionDraft? editor;
    private bool adding, updating, busy, available;
    internal event Action? Changed;
    internal event Action? Saved;
    // The host may need to resolve unsaved surrounding settings before opening this form.
    internal Action<Action>? BeforeEdit { get; set; }
    internal bool Editing => editor != null;
    internal string TestName => name.Text ?? "";
    internal bool HasChanges => editor != null && (adding || !JToken.DeepEquals(baseline, EditorValues()));

    internal static FrameworkElement Create(Func<string, string> text)
    {
        using var stream = typeof(AiConnectionsView).Assembly.GetManifestResourceStream("SeaPowerAIAdvisor.Shared.Connections.xaml")
            ?? throw new FileNotFoundException("The common connection layout is missing.");
        using var reader = new StreamReader(stream); var markup = reader.ReadToEnd();
        var root = GUI.ParseXaml(markup) as FrameworkElement ?? throw new InvalidDataException("Invalid connection layout.");
        foreach (Match match in Regex.Matches(markup, @"DynamicResource AiConnection\.([^}]+)"))
        { var key = match.Groups[1].Value; root.Resources["AiConnection." + key] = text(key); }
        return root;
    }
    internal AiConnectionsView(FrameworkElement root, IAiConnectionStore store, Func<IReadOnlyList<IAiConnectionProvider>> providers,
        Func<string, string> text, Func<string> language, TypingScope typing, Action<Action> guard, Func<bool> canCommit,
        Action<string, Action> confirmDelete, Action<Action> confirmDuplicate, Action<AiConnectionException> error,
        IAiConnectionEditorOptions? options = null, Func<string, string?>? role = null)
    {
        Root = root; this.store = store; this.providers = providers; this.text = text; this.language = language; this.typing = typing; focus = new DeferredInputFocus(typing);
        this.guard = guard; this.canCommit = canCommit; this.confirmDelete = confirmDelete; this.confirmDuplicate = confirmDuplicate;
        this.error = error; this.options = options; this.role = role ?? (_ => null);
        name = Find<TextBox>("ApiConfigurationName"); timeout = Find<TextBox>("RequestTimeout"); outputTokens = Find<TextBox>("OutputTokens"); prompt = Find<TextBox>("ConnectionPrompt");
        provider = Find<ComboBox>("ApiProvider"); fields = Find<StackPanel>("ApiProviderFields");
        listPane = Find<FrameworkElement>("ApiConnectionList"); editorPane = Find<FrameworkElement>("ApiEditor");
        description = Find<TextBlock>("ApiProviderDescription"); descriptionBox = Find<FrameworkElement>("ApiProviderDescriptionBox"); providerError = Find<TextBlock>("ApiProviderError");
        add = Find<Button>("AddApiConfiguration"); duplicate = Find<Button>("DuplicateApiConfiguration"); remove = Find<Button>("RemoveApiConfiguration");
        save = Find<Button>("SaveApiEntry"); cancel = Find<Button>("CancelApiEntry");
        cards = new AiConnectionListView(Find<StackPanel>("ApiConnectionCards"), text, language, id => Run(() => BeginEdit(() => store.Edit(id), false)), guard);
        name.PreviewGotKeyboardFocus += (_, args) => { if (focus.Pending(name)) args.Handled = true; };
        prompt.MaxLength = AiConnectionProfile.MaxPromptLength; Numeric(timeout); Numeric(outputTokens);
        void Bind(Button button, Action action) => button.Click += (_, _) => guard(() => Run(action));
        Bind(add, () => BeginEdit(() => store.Create(text("connection.configuration_name_pattern")), true));
        Bind(cancel, () => { Refresh(); Changed?.Invoke(); });
        Bind(save, () => { if (!canCommit()) return; store.Save(ReadEditor(), adding); Finish(); });
        Bind(duplicate, Duplicate);
        Bind(remove, () => { if (editor == null || adding || busy) return; var id = editor.Id;
            confirmDelete(store.ReadConnections().Single(c => c.Id == id).Name, () => Run(() => { store.Remove(id); Finish(); })); });
        foreach (var input in new[] { name, timeout, outputTokens, prompt })
        { input.TextChanged += (_, _) => guard(NotifyChanged); input.GotKeyboardFocus += (_, _) => guard(() => typing.Acquire(input)); input.LostKeyboardFocus += (_, _) => guard(() => typing.Release(input)); }
        provider.SelectionChanged += (_, _) => guard(() =>
        {
            if (updating || editor == null || provider.SelectedIndex < 0 || provider.SelectedIndex >= choices.Count) return;
            ReadEditor(false); providerDrafts[editor.Provider] = (JObject)editor.Settings.DeepClone(); var selected = choices[provider.SelectedIndex];
            editor.Provider = selected.Id; editor.Settings = providerDrafts.TryGetValue(selected.Id, out var old) ? (JObject)old.DeepClone() : selected.Defaults();
            ShowFields(); Changed?.Invoke();
        });
    }
    internal void Tick() => focus.Tick();
    internal void Open() { available = true; Refresh(); }
    internal void Close() { available = false; Refresh(); }
    internal void SetBusy(bool value) { busy = value; UpdateEnabled(); }
    internal void UpdateCards() => cards.Show(store.ReadConnections(), choices: providers(), role);
    internal AiConnectionDraft ReadEditor(bool limits = true)
    {
        var entry = editor ?? throw new AiConnectionException("api_selection_invalid");
        entry.Name = name.Text ?? ""; entry.Prompt = prompt.Text ?? ""; options?.Read(entry);
        if (limits) { entry.RequestTimeoutSeconds = AiRequestTimeout.Parse(timeout.Text ?? ""); entry.MaxOutputTokens = AiOutputTokens.Parse(outputTokens.Text ?? ""); }
        foreach (var item in values) entry.Settings[item.Key] = item.Value().Trim(); return entry;
    }
    private void Run(Action action) { try { action(); } catch (AiConnectionException failure) { error(failure); } }
    private void NotifyChanged() { if (!updating) Changed?.Invoke(); }
    private JObject EditorValues() => new() { ["name"] = name.Text ?? "", ["provider"] = editor!.Provider,
        ["timeout"] = timeout.Text ?? "", ["output_tokens"] = outputTokens.Text ?? "", ["prompt"] = prompt.Text ?? "",
        ["options"] = options?.Values(), ["fields"] = new JObject(values.Select(f => new JProperty(f.Key, f.Value()))) };
    private void ShowFields()
    {
        typing.Release(); fields.Children.Clear(); values.Clear(); var service = choices.SingleOrDefault(p => p.Id == editor!.Provider);
        description.Text = service?.Description(text, language()) ?? ""; descriptionBox.Visibility = string.IsNullOrWhiteSpace(description.Text) ? Visibility.Collapsed : Visibility.Visible;
        providerError.Text = service == null ? text("connection.provider_unavailable") : ""; providerError.Visibility = service == null ? Visibility.Visible : Visibility.Collapsed;
        options?.Show(editor!, service); if (service == null) return;
        foreach (var field in service.Fields)
        {
            fields.Children.Add(new TextBlock { Text = field.Caption(text, language()), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 4) });
            var value = (string?)editor!.Settings[field.Key] ?? field.DefaultValue;
            if (field.Secret)
            {
                var input = new PasswordBox { FontSize = 14, Padding = new Thickness(7), MaxLength = 512, ShowLastCharacterDuration = 0,
                    Background = (Brush)fields.FindResource("Brush.TextBox.Background"), CaretBrush = (Brush)fields.FindResource("Brush.TextBox.ForegroundFocus"), Password = value };
                if (field.Numeric) Numeric(input); input.PasswordChanged += (_, _) => guard(NotifyChanged);
                input.GotKeyboardFocus += (_, _) => guard(() => typing.Acquire(input)); input.LostKeyboardFocus += (_, _) => guard(() => typing.Release(input));
                values.Add(field.Key, () => input.Password); fields.Children.Add(input);
            }
            else
            {
                var input = new TextBox { FontSize = 14, Padding = new Thickness(7), MaxLength = 2048, Text = value }; if (field.Numeric) Numeric(input);
                input.TextChanged += (_, _) => guard(NotifyChanged); input.GotKeyboardFocus += (_, _) => guard(() => typing.Acquire(input)); input.LostKeyboardFocus += (_, _) => guard(() => typing.Release(input));
                values.Add(field.Key, () => input.Text ?? ""); fields.Children.Add(input);
            }
        }
    }
    private static void Numeric(FrameworkElement input) { input.Width = 140; input.HorizontalAlignment = HorizontalAlignment.Left; }
    private void UpdateEnabled()
    {
        listPane.IsEnabled = add.IsEnabled = available && !busy && !Editing;
        duplicate.IsEnabled = remove.IsEnabled = Editing && !adding && !busy;
        name.IsEnabled = timeout.IsEnabled = outputTokens.IsEnabled = prompt.IsEnabled = provider.IsEnabled = fields.IsEnabled = save.IsEnabled = cancel.IsEnabled = Editing && !busy;
        options?.SetEnabled(Editing && !busy);
    }
    private void Refresh()
    {
        updating = true;
        try
        {
            focus.Cancel(); typing.Release(); editor = null; baseline = null; providerDrafts.Clear(); fields.Children.Clear(); values.Clear(); provider.Items.Clear();
            listPane.Visibility = Visibility.Visible; editorPane.Visibility = Visibility.Collapsed;
            name.Text = timeout.Text = outputTokens.Text = prompt.Text = "";
            if (available) cards.Show(store.ReadConnections(), providers(), role);
            else cards.Clear();
        }
        finally { updating = false; }
        UpdateEnabled();
    }
    private void BeginEdit(Func<AiConnectionDraft> create, bool isNew)
    {
        if (!available || Editing || busy) return;
        void Open() => ShowEditor(create(), isNew);
        if (BeforeEdit == null) Open(); else BeforeEdit(() => guard(() => Run(Open)));
    }
    private void ShowEditor(AiConnectionDraft entry, bool isNew)
    {
        if (Editing || busy) return; editor = entry; adding = isNew; updating = true;
        try
        {
            name.Text = entry.Name; prompt.Text = entry.Prompt; timeout.Text = entry.RequestTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            outputTokens.Text = entry.MaxOutputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture); choices = providers(); provider.Items.Clear();
            foreach (var service in choices) provider.Items.Add(new ComboBoxItem { Content = service.Caption(text, language()) });
            var index = choices.ToList().FindIndex(p => p.Id == entry.Provider);
            if (index < 0) { provider.Items.Add(new ComboBoxItem { Content = entry.Provider }); index = choices.Count; }
            provider.SelectedIndex = index; ShowFields();
        }
        finally { updating = false; }
        listPane.Visibility = Visibility.Collapsed; editorPane.Visibility = Visibility.Visible; baseline = EditorValues();
        save.Content = text(adding ? "settings.register" : "settings.save_form"); UpdateEnabled(); Changed?.Invoke(); typing.Release(); focus.Request(name);
    }
    private void Finish() { Refresh(); Changed?.Invoke(); Saved?.Invoke(); add.Focus(); }
    private void Duplicate()
    {
        if (editor == null || adding || busy || !canCommit()) return; var id = editor.Id;
        void Copy() => Run(() => { store.Duplicate(id, text("connection.configuration_copy_suffix")); Finish(); });
        if (!JToken.DeepEquals(baseline, EditorValues())) confirmDuplicate(Copy); else Copy();
    }
}
