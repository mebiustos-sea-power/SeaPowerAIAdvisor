using System;
using System.Linq;
using Noesis;

namespace SeaPowerAIAdvisor;

// Registration forms and role selections are separate operations; no whole-settings draft is saved here.
internal sealed class AdvisorApiSettingsView
{
    private readonly ComboBox main, sub, selector;
    private readonly TextBox name, model;
    private readonly PasswordBox key;
    private readonly Button add, edit, remove, saveEntry, cancelEntry, saveAssignments;
    private readonly FrameworkElement editorPane, useTab;
    private readonly AdvisorApiSettingsStore store;
    private readonly Func<AdvisorText> text;
    private readonly Action changed, saved, compositionNotice;
    private readonly Action<AdvisorConnectionException> error;
    private readonly Action<string, Action> confirmDelete;
    private readonly TypingScope typing;
    private AdvisorSettingsDraft? data;
    private AdvisorApiConfigurationDraft? editor;
    private string[] ids = Array.Empty<string>();
    private bool updating, busy, adding;
    internal bool Editing => editor != null;
    internal string TestName => name.Text ?? "";
    private string? MainId => main.SelectedIndex <= 0 ? null : ids[main.SelectedIndex - 1];
    private string? SubId => sub.SelectedIndex <= 0 ? null : ids[sub.SelectedIndex - 1];
    private string? SelectedId => selector.SelectedIndex < 0 ? null : ids[selector.SelectedIndex];
    internal bool HasChanges => Editing || (data != null && (MainId != data.MainId || SubId != data.SubId));

    internal AdvisorApiSettingsView(FrameworkElement content, AdvisorApiSettingsStore store, Func<AdvisorText> text,
        Action<Action> guard, TypingScope typing, Action changed, Action saved, Action compositionNotice, Action<AdvisorConnectionException> error, Action<string, Action> confirmDelete)
    {
        this.store = store; this.text = text; this.typing = typing; this.changed = changed;
        this.saved = saved; this.compositionNotice = compositionNotice; this.error = error; this.confirmDelete = confirmDelete;
        T Named<T>(string id) where T : class => content.FindName(id) as T ?? throw new InvalidOperationException("Missing API control: " + id);
        main = Named<ComboBox>("MainApiConfiguration"); sub = Named<ComboBox>("SubApiConfiguration"); selector = Named<ComboBox>("ApiConfigurationSelector");
        name = Named<TextBox>("ApiConfigurationName"); model = Named<TextBox>("ModelName"); key = Named<PasswordBox>("ApiKey");
        add = Named<Button>("AddApiConfiguration"); edit = Named<Button>("EditApiConfiguration"); remove = Named<Button>("RemoveApiConfiguration");
        saveEntry = Named<Button>("SaveApiEntry"); cancelEntry = Named<Button>("CancelApiEntry"); saveAssignments = Named<Button>("SaveApiAssignments");
        editorPane = Named<FrameworkElement>("ApiEditor"); useTab = Named<FrameworkElement>("ApiUseTab");
        void Bind(Button button, Action action) => button.Click += (_, _) => guard(() => Run(action));
        Bind(add, Add); Bind(edit, Edit); Bind(remove, Remove); Bind(saveEntry, SaveEntry); Bind(cancelEntry, CancelEntry); Bind(saveAssignments, SaveAssignments);
        selector.SelectionChanged += (_, _) => guard(() => { if (!updating) SetBusy(busy); });
        name.TextChanged += (_, _) => guard(NotifyChanged); model.TextChanged += (_, _) => guard(NotifyChanged); key.PasswordChanged += (_, _) => guard(NotifyChanged);
        foreach (var input in new[] { name, model })
        { input.GotKeyboardFocus += (_, _) => guard(() => typing.Acquire(input)); input.LostKeyboardFocus += (_, _) => guard(() => typing.Release(input)); }
        key.GotKeyboardFocus += (_, _) => guard(() => typing.Acquire(key)); key.LostKeyboardFocus += (_, _) => guard(() => typing.Release(key));
    }
    private void Run(Action action) { try { action(); } catch (AdvisorConnectionException failure) { error(failure); } }
    internal void Open() { editor = null; Refresh(store.Read(), null, false); }
    internal void Close() { editor = null; Refresh(null, null, false); }
    internal void Focus() { if (main.IsEnabled) main.Focus(); }
    internal AdvisorSettings TestSettings()
    { ReadEditor(); return store.Test(editor ?? throw new AdvisorConnectionException("api_selection_invalid")); }
    private void ReadEditor()
    { if (editor != null) { editor.Name = name.Text ?? ""; editor.ApiKey = key.Password; editor.Model = model.Text ?? ""; } }
    internal void SetBusy(bool value)
    {
        busy = value;
        main.IsEnabled = sub.IsEnabled = saveAssignments.IsEnabled = useTab.IsEnabled = data != null && !busy && !Editing;
        selector.IsEnabled = add.IsEnabled = data != null && !busy && !Editing;
        edit.IsEnabled = remove.IsEnabled = data != null && SelectedId != null && !busy && !Editing;
        name.IsEnabled = model.IsEnabled = key.IsEnabled = saveEntry.IsEnabled = cancelEntry.IsEnabled = Editing && !busy;
    }
    private void Refresh(AdvisorSettingsDraft? value, string? selection, bool keepAssignments)
    {
        var selectedMain = keepAssignments ? MainId : value?.MainId;
        var selectedSub = keepAssignments ? SubId : value?.SubId;
        updating = true;
        try
        {
            data = value; ids = value?.Configurations.Select(c => c.Id).ToArray() ?? Array.Empty<string>();
            main.Items.Clear(); sub.Items.Clear(); selector.Items.Clear();
            main.Items.Add(new ComboBoxItem { Content = text()["connection.choose_configuration"] });
            sub.Items.Add(new ComboBoxItem { Content = text()["connection.no_sub"] });
            foreach (var id in ids)
                foreach (var control in new[] { main, sub, selector }) control.Items.Add(new ComboBoxItem { Content = value!.Find(id)!.Name });
            main.SelectedIndex = Array.IndexOf(ids, selectedMain) + 1; sub.SelectedIndex = Array.IndexOf(ids, selectedSub) + 1;
            selector.SelectedIndex = selection == null ? (ids.Length == 0 ? -1 : 0) : Array.IndexOf(ids, selection);
            editorPane.Visibility = Visibility.Collapsed; name.Text = model.Text = ""; key.Password = "";
        }
        finally { updating = false; }
        SetBusy(busy);
    }
    private void NotifyChanged() { if (!updating) changed(); }
    private void ShowEditor(AdvisorApiConfigurationDraft value, bool isNew)
    {
        editor = value; adding = isNew; updating = true;
        try { name.Text = value.Name; model.Text = value.Model; key.Password = value.ApiKey; }
        finally { updating = false; }
        editorPane.Visibility = Visibility.Visible; saveEntry.Content = text()[adding ? "settings.register" : "settings.save_form"];
        SetBusy(false); changed(); typing.Release(); name.Focus();
    }
    private void Add() { if (data != null && !Editing) ShowEditor(store.Read().Add(text()["connection.configuration_name_pattern"]), true); }
    private void Edit()
    {
        if (SelectedId == null || Editing) return;
        var entry = store.Read().Find(SelectedId) ?? throw new AdvisorConnectionException("api_selection_invalid");
        ShowEditor(new AdvisorApiConfigurationDraft(entry.Build()), false);
    }
    private void CancelEntry()
    { var id = adding ? SelectedId : editor?.Id; editor = null; Refresh(store.Read(), id, true); changed(); }
    private void SaveEntry()
    {
        if (QuestionInputController.IsComposing) { compositionNotice(); return; }
        ReadEditor(); var entry = editor ?? throw new AdvisorConnectionException("api_selection_invalid");
        var result = store.SaveEntry(entry, adding); editor = null; Refresh(result, entry.Id, true); changed(); saved();
    }
    private void SaveAssignments()
    { var result = store.Assign(MainId, SubId); Refresh(result, SelectedId, false); changed(); saved(); }
    private void Remove()
    {
        if (SelectedId == null || Editing) return;
        var id = SelectedId; var caption = data!.Find(id)!.Name;
        confirmDelete(caption, () => Run(() => { var result = store.Remove(id); Refresh(result, null, true); changed(); saved(); }));
    }
}
