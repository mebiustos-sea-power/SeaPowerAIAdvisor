using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Noesis;

namespace SeaPowerAIAdvisor;

// One profile form owns its unsaved text/image; completed library operations are immediately durable.
internal sealed class AdvisorProfileSettingsView
{
    private readonly string directory;
    private readonly AdvisorProfileSettingsStore store;
    private readonly Func<AdvisorText> text;
    private readonly Func<byte[]> defaultPortrait;
    private readonly Action<AdvisorProfile> saved;
    private readonly Action changed;
    private readonly Action<AdvisorProfileException> error;
    private readonly Action<string, Action> confirmDelete;
    private readonly TypingScope typing;
    private readonly ComboBox active, selector;
    private readonly Button add, edit, remove, use, saveEntry, cancelEntry, choose;
    private readonly TextBox name, role, personality;
    private readonly FrameworkElement editorPane, useTab;
    private readonly Image image;
    private readonly TextBlock imageError, status;
    private AdvisorProfile? data;
    private AdvisorProfileDraft? form;
    private AdvisorPortrait? preview;
    private Task<string?>? selection;
    private string[] ids = Array.Empty<string>();
    private bool updating, adding;
    private (string Name, string Role, string Personality) editorBaseline;
    private (string Name, string Role, string Personality) EditorValues => (name.Text ?? "", role.Text ?? "", personality.Text ?? "");
    private string? SelectedId => selector.SelectedIndex < 0 ? null : ids[selector.SelectedIndex];
    private string? ActiveId => active.SelectedIndex < 0 ? null : ids[active.SelectedIndex];
    internal bool HasChanges(int tab) => tab == 0 ? data != null && ActiveId != data.ActiveId :
        form != null && (adding || EditorValues != editorBaseline || form.HasSelectedPortrait);
    internal AdvisorProfileSettingsView(FrameworkElement content, string directory, Func<AdvisorText> text, Func<byte[]> defaultPortrait,
        Action<Action> guard, TypingScope typing, Action<AdvisorProfile> saved, Action changed, Action<AdvisorProfileException> error,
        Action<string, Action> confirmDelete, Action<string> warn)
    {
        this.directory = directory; this.text = text; this.defaultPortrait = defaultPortrait; this.typing = typing;
        this.saved = saved; this.changed = changed; this.error = error; this.confirmDelete = confirmDelete; store = new AdvisorProfileSettingsStore(directory, warn);
        T Named<T>(string id) where T : class => content.FindName(id) as T ?? throw new InvalidOperationException("Missing profile control: " + id);
        active = Named<ComboBox>("ActiveAdvisorSelector"); selector = Named<ComboBox>("AdvisorSelector");
        add = Named<Button>("AddAdvisor"); edit = Named<Button>("EditAdvisor"); remove = Named<Button>("RemoveAdvisor"); use = Named<Button>("SaveAdvisorSelection");
        saveEntry = Named<Button>("SaveProfileEntry"); cancelEntry = Named<Button>("CancelProfileEntry"); choose = Named<Button>("ChoosePortrait");
        name = Named<TextBox>("ProfileName"); role = Named<TextBox>("ProfileRole"); personality = Named<TextBox>("ProfilePersonality");
        personality.MaxLength = AdvisorProfile.MaxPersonalityLength;
        image = Named<Image>("ProfilePreview"); imageError = Named<TextBlock>("ProfilePreviewError"); status = Named<TextBlock>("ProfileStatus");
        editorPane = Named<FrameworkElement>("ProfileEditor"); useTab = Named<FrameworkElement>("AdvisorUseTab");
        void Bind(Button button, Action action) => button.Click += (_, _) => guard(() => Run(action));
        Bind(add, Add); Bind(edit, Edit); Bind(remove, Remove); Bind(use, Use); Bind(saveEntry, SaveEntry); Bind(cancelEntry, CancelEntry); Bind(choose, Choose);
        selector.SelectionChanged += (_, _) => guard(() => { if (!updating) Controls(); });
        foreach (var input in new[] { name, role, personality })
        { input.GotKeyboardFocus += (_, _) => guard(() => typing.Acquire(input)); input.LostKeyboardFocus += (_, _) => guard(() => typing.Release(input)); }
    }
    private void Run(Action action) { try { action(); } catch (AdvisorProfileException failure) { error(failure); } }
    internal void Open() { var saved = store.Read(); form = null; selection = null; Refresh(saved, null, false); }
    internal void Close() { form = null; selection = null; Refresh(null, null, false); }
    internal void Focus() { if (active.IsEnabled) active.Focus(); }
    private void Controls()
    {
        var editing = form != null; var available = data != null && !editing;
        useTab.IsEnabled = data != null;
        active.IsEnabled = use.IsEnabled = available;
        selector.IsEnabled = add.IsEnabled = available;
        edit.IsEnabled = available && SelectedId != null; remove.IsEnabled = available && SelectedId != null && SelectedId != AdvisorProfile.DefaultId;
        saveEntry.IsEnabled = cancelEntry.IsEnabled = editing && selection == null;
        name.IsReadOnly = role.IsReadOnly = form?.UseDefault != false;
        choose.Visibility = form?.UseDefault == false ? Visibility.Visible : Visibility.Collapsed;
        choose.IsEnabled = editing && selection == null;
    }
    private void Refresh(AdvisorProfile? value, string? selected, bool keepActive)
    {
        var activeId = keepActive ? ActiveId : value?.ActiveId;
        updating = true;
        try
        {
            data = value;
            ids = value == null ? Array.Empty<string>() : new[] { AdvisorProfile.DefaultId }.Concat(value.Advisers.Select(a => a.Id)).ToArray();
            selector.Items.Clear(); active.Items.Clear();
            foreach (var id in ids)
            {
                var caption = id == AdvisorProfile.DefaultId ? text().Format("profile.default_choice", AdvisorProfile.DefaultName) : value!.Advisers.Single(a => a.Id == id).Name;
                selector.Items.Add(new ComboBoxItem { Content = caption }); active.Items.Add(new ComboBoxItem { Content = caption });
            }
            active.SelectedIndex = Array.IndexOf(ids, activeId); if (value != null && active.SelectedIndex < 0) active.SelectedIndex = 0;
            selector.SelectedIndex = selected == null ? (ids.Length > 0 ? 0 : -1) : Array.IndexOf(ids, selected);
            editorPane.Visibility = Visibility.Collapsed; name.Text = role.Text = personality.Text = ""; status.Text = "";
            ReleasePreview();
        }
        finally { updating = false; }
        Controls();
    }
    private void Begin(AdvisorProfileDraft value, bool isNew)
    {
        form = value; adding = isNew;
        name.Text = value.UseDefault ? AdvisorProfile.DefaultName : value.CustomName;
        role.Text = value.UseDefault ? text()["advisor.role"] : value.CustomRole; personality.Text = value.Personality;
        editorBaseline = EditorValues;
        editorPane.Visibility = Visibility.Visible; saveEntry.Content = text()[adding ? "settings.register" : "settings.save_form"];
        changed(); Controls(); Preview(); typing.Release(); (value.UseDefault ? personality : name).Focus();
    }
    private void ReadForm()
    { if (form == null) return; if (!form.UseDefault) { form.CustomName = name.Text ?? ""; form.CustomRole = role.Text ?? ""; } form.Personality = personality.Text ?? ""; }
    private void Add() { var draft = new AdvisorProfileDraft(store.Read()); draft.Add(text()["profile.new_name"], text()["advisor.role"]); Begin(draft, true); }
    private void Edit() { if (SelectedId == null) return; var draft = new AdvisorProfileDraft(store.Read()); draft.Select(SelectedId); Begin(draft, false); }
    private void CancelEntry() { var id = adding ? SelectedId : form?.SelectedId; form = null; Refresh(store.Read(), id, true); changed(); }
    private void SaveEntry()
    {
        if (form == null || selection != null) return;
        if (QuestionInputController.IsComposing) { status.Text = text()["profile.ime"]; status.Visibility = Visibility.Visible; return; }
        ReadForm(); var id = form.SelectedId; _ = form.Build();
        using (var verified = new AdvisorPortrait(form.ReadActivePortrait(directory, defaultPortrait))) { }
        var next = store.SaveEntry(form, id, adding); form = null; Refresh(next, id, true); saved(next);
    }
    private void Use()
    {
        if (ActiveId == null) return;
        var candidate = new AdvisorProfileDraft(store.Read()); candidate.Select(ActiveId);
        using (var verified = new AdvisorPortrait(candidate.ReadActivePortrait(directory, defaultPortrait))) { }
        var next = store.Select(ActiveId); Refresh(next, SelectedId, false); saved(next);
    }
    private void Remove()
    {
        if (SelectedId == null || SelectedId == AdvisorProfile.DefaultId) return;
        var id = SelectedId; var caption = data!.Advisers.Single(a => a.Id == id).Name;
        confirmDelete(caption, () => Run(() => { var next = store.Remove(id); Refresh(next, null, true); saved(next); }));
    }
    private void Preview()
    {
        ReleasePreview();
        if (form == null) return;
        try { preview = new AdvisorPortrait(form.ReadActivePortrait(directory, defaultPortrait)); image.Source = preview.Source; imageError.Visibility = Visibility.Collapsed; }
        catch (AdvisorProfileException failure) { imageError.Visibility = Visibility.Visible; error(failure); }
    }
    private void ReleasePreview() { image.Source = null; preview?.Dispose(); preview = null; }
    private void Choose()
    {
        if (form?.UseDefault != false || selection != null) return;
        typing.Release(); selection = PortraitFileDialog.OpenAsync(text()["profile.choose_title"], text()["profile.image_filter"]); Controls();
    }
    internal void Tick()
    {
        if (selection == null || !selection.IsCompleted) return;
        var task = selection; selection = null; Controls();
        try
        {
            var path = task.GetAwaiter().GetResult(); if (path == null || form == null) return;
            var bytes = new AdvisorProfile("Selected portrait", path).ReadPortrait(directory);
            using (var verified = new AdvisorPortrait(bytes)) { }
            form.SelectPortrait(bytes); changed(); Preview(); status.Text = text()["profile.image_selected"]; status.Visibility = Visibility.Visible;
        }
        catch (Win32Exception) { status.Text = text()["profile.error.portrait_dialog_failed"]; status.Visibility = Visibility.Visible; }
        catch (AdvisorProfileException failure) { error(failure); }
    }
}
