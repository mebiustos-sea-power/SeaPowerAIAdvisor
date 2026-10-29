using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Noesis;

namespace SeaPowerAIAdvisor;

// Advisor owns role assignments and situation delivery; common controls own connection editing.
internal sealed class AdvisorApiSettingsView
{
    private readonly AdvisorApiSettingsStore store;
    private readonly Func<AdvisorText> text;
    private readonly ComboBox main, sub, translation;
    private readonly FrameworkElement useTab;
    private readonly Button saveAssignments;
    private readonly AiConnectionsView connections;
    private readonly Action changed, saved;
    private readonly Action<AiConnectionException> error;
    private string[] ids = Array.Empty<string>();
    private AdvisorSettingsDraft? data;
    private bool busy;
    internal bool Editing => connections.Editing;
    internal string TestName => connections.TestName;
    private string? MainId => main.SelectedIndex <= 0 ? null : ids[main.SelectedIndex - 1];
    private string? SubId => sub.SelectedIndex <= 0 ? null : ids[sub.SelectedIndex - 1];
    private string? TranslationId => translation.SelectedIndex <= 0 ? null : translation.SelectedIndex == 1 ? AdvisorApiLibrary.FollowAdvisor : ids[translation.SelectedIndex - 2];
    internal bool HasChanges(int tab) => tab == 0 ? data != null && (MainId != data.MainId || SubId != data.SubId || TranslationId != data.TranslationId) : tab == 1 && connections.HasChanges;
    internal AdvisorApiSettingsView(FrameworkElement content, AdvisorApiSettingsStore store, Func<AdvisorText> text,
        Action<Action> guard, TypingScope typing, Action changed, Action saved, Action compositionNotice,
        Action<AiConnectionException> error, Action<string, Action> confirmDelete, Action<Action> confirmDuplicate)
    {
        this.store = store; this.text = text; this.changed = changed; this.saved = saved; this.error = error;
        T Named<T>(string id) where T : class => content.FindName(id) as T ?? throw new InvalidOperationException("Missing API control: " + id);
        main = Named<ComboBox>("MainApiConfiguration"); sub = Named<ComboBox>("SubApiConfiguration"); translation = Named<ComboBox>("TranslationApiConfiguration");
        useTab = Named<FrameworkElement>("ApiUseTab"); saveAssignments = Named<Button>("SaveApiAssignments");
        var root = AiConnectionsView.Create(key => text()[key]); Named<ContentControl>("ApiRegistration").Content = root;
        var delivery = new AdvisorConnectionDeliveryView((StackPanel)root.FindName("ConnectionOptions"), text, guard, changed);
        connections = new AiConnectionsView(root, store, () => AdvisorServiceCatalog.BuiltIn.Services, key => text()[key], () => text().Language,
            typing, guard, () => { if (!QuestionInputController.IsComposing) return true; compositionNotice(); return false; },
            confirmDelete, confirmDuplicate, error, delivery, Role);
        connections.Changed += () => { SetBusy(busy); changed(); };
        connections.Saved += () => { RefreshAssignments(true); saved(); };
        saveAssignments.Click += (_, _) => guard(() =>
        {
            try { store.Assign(MainId, SubId, TranslationId); RefreshAssignments(false); connections.UpdateCards(); changed(); saved(); }
            catch (AiConnectionException failure) { error(failure); }
        });
    }
    internal T Find<T>(string id) where T : class => connections.Find<T>(id);
    internal void Tick() => connections.Tick();
    internal void Open() { RefreshAssignments(false); connections.Open(); SetBusy(busy); }
    internal void Close() { data = null; connections.Close(); SetBusy(busy); }
    internal void Focus() { if (main.IsEnabled) main.Focus(); }
    internal AdvisorApiConfiguration TestConfiguration() => ((AdvisorApiConfigurationDraft)connections.ReadEditor()).Build();
    internal AdvisorConnectionOptions TestOptions() => store.TestOptions();
    internal void SetBusy(bool value)
    {
        busy = value; useTab.IsEnabled = data != null;
        main.IsEnabled = sub.IsEnabled = translation.IsEnabled = saveAssignments.IsEnabled = data != null && !busy && !Editing;
        connections.SetBusy(value);
    }
    private string? Role(string id)
    {
        if (data == null) return null;
        var roles = new System.Collections.Generic.List<string>();
        if (id == data.MainId) roles.Add(text()["connection.card_main"]);
        if (id == data.TranslationConnectionId) roles.Add(text()["connection.card_translation"]);
        if (id == data.SubId) roles.Add(text()["connection.card_sub"]);
        return roles.Count == 0 ? null : string.Join(" / ", roles);
    }
    private void RefreshAssignments(bool keep)
    {
        var selectedMain = keep ? MainId : null; var selectedSub = keep ? SubId : null; var selectedTranslation = keep ? TranslationId : null;
        data = store.Read(); if (!keep) { selectedMain = data.MainId; selectedSub = data.SubId; selectedTranslation = data.TranslationId; }
        var entries = data.ConfigurationsByName.ToArray(); ids = entries.Select(c => c.Id).ToArray();
        main.Items.Clear(); sub.Items.Clear(); translation.Items.Clear(); main.Items.Add(new ComboBoxItem { Content = text()["connection.choose_configuration"] });
        sub.Items.Add(new ComboBoxItem { Content = text()["connection.no_sub"] });
        translation.Items.Add(new ComboBoxItem { Content = text()["connection.translation_disabled"] });
        translation.Items.Add(new ComboBoxItem { Content = text()["connection.same_as_advisor"] });
        foreach (var entry in entries) foreach (var control in new[] { main, sub, translation }) control.Items.Add(new ComboBoxItem { Content = entry.Name });
        main.SelectedIndex = Array.IndexOf(ids, selectedMain) + 1; sub.SelectedIndex = Array.IndexOf(ids, selectedSub) + 1;
        var translationIndex = Array.IndexOf(ids, selectedTranslation);
        translation.SelectedIndex = selectedTranslation == AdvisorApiLibrary.FollowAdvisor ? 1 : translationIndex < 0 ? 0 : translationIndex + 2;
    }
}

internal sealed class AdvisorConnectionDeliveryView : IAiConnectionEditorOptions
{
    private static readonly AdvisorSituationDelivery[] choices = { AdvisorSituationDelivery.Upfront, AdvisorSituationDelivery.OnDemand, AdvisorSituationDelivery.OnRequest };
    private readonly StackPanel host;
    private readonly ComboBox selector = new() { MinHeight = 30 };
    private readonly Func<AdvisorText> text;
    private bool updating;
    internal AdvisorConnectionDeliveryView(StackPanel host, Func<AdvisorText> text, Action<Action> guard, Action changed)
    {
        this.host = host; this.text = text; host.Margin = new Thickness(0, 12, 0, 0);
        host.Children.Add(new TextBlock { Text = text()["connection.situation_delivery"], FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        host.Children.Add(new TextBlock { Text = text()["connection.situation_delivery_hint"], FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = 0.85f, Margin = new Thickness(0, 0, 0, 6) });
        host.Children.Add(selector); selector.SelectionChanged += (_, _) => guard(() => { if (!updating) changed(); });
    }
    public void Show(AiConnectionDraft entry, IAiConnectionProvider? provider)
    {
        var draft = (AdvisorApiConfigurationDraft)entry; var service = provider as AdvisorService;
        host.Visibility = service?.Independent == false ? Visibility.Visible : Visibility.Collapsed;
        if (service?.Independent == true) draft.SituationDelivery = AdvisorSituationDelivery.OnDemand;
        updating = true;
        try { selector.Items.Clear(); foreach (var choice in choices) selector.Items.Add(new ComboBoxItem { Content = text()["connection.situation_" + choice.Code()] });
            selector.SelectedIndex = Array.IndexOf(choices, draft.SituationDelivery); }
        finally { updating = false; }
    }
    public void Read(AiConnectionDraft entry) { if (selector.SelectedIndex >= 0) ((AdvisorApiConfigurationDraft)entry).SituationDelivery = choices[selector.SelectedIndex]; }
    public JToken Values() => new JValue(selector.SelectedIndex);
    public void SetEnabled(bool enabled) => selector.IsEnabled = enabled;
}
