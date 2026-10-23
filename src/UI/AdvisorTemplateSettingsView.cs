using System;
using System.Collections.Generic;
using System.Linq;
using Noesis;

namespace SeaPowerAIAdvisor;

// The form owns unsaved edits; each explicit save or delete updates only the template library.
internal sealed class AdvisorTemplateSettingsView
{
    private readonly AdvisorTextTemplateStore store;
    private readonly Func<AdvisorText> text;
    private readonly Action saved, changed, composing;
    private readonly Action<AdvisorTemplateException> error;
    private readonly Action<string, Action> confirmDelete;
    private readonly TypingScope typing;
    private readonly ComboBox selector;
    private readonly Button add, edit, remove, save, cancel;
    private readonly TextBox name, message;
    private readonly FrameworkElement editor;
    private IReadOnlyList<AdvisorTextTemplate> entries = Array.Empty<AdvisorTextTemplate>();
    private string? editingId;
    private bool adding;
    private AdvisorTextTemplate? Selected => selector.SelectedIndex >= 0 && selector.SelectedIndex < entries.Count ? entries[selector.SelectedIndex] : null;
    internal bool HasChanges => editingId != null;

    internal AdvisorTemplateSettingsView(FrameworkElement content, AdvisorTextTemplateStore store, Func<AdvisorText> text,
        Action<Action> guard, TypingScope typing, Action saved, Action changed, Action composing,
        Action<AdvisorTemplateException> error, Action<string, Action> confirmDelete)
    {
        this.store = store; this.text = text; this.typing = typing; this.saved = saved; this.changed = changed;
        this.composing = composing; this.error = error; this.confirmDelete = confirmDelete;
        T Named<T>(string id) where T : class => content.FindName(id) as T ?? throw new InvalidOperationException("Missing template control: " + id);
        selector = Named<ComboBox>("TemplateSelector"); name = Named<TextBox>("TemplateName"); message = Named<TextBox>("TemplateMessage");
        name.MaxLength = AdvisorTextTemplate.MaxNameLength; message.MaxLength = AdvisorConversation.MaxQuestionLength;
        editor = Named<FrameworkElement>("TemplateEditor");
        add = Named<Button>("AddTemplate"); edit = Named<Button>("EditTemplate"); remove = Named<Button>("RemoveTemplate");
        save = Named<Button>("SaveTemplate"); cancel = Named<Button>("CancelTemplate");
        void Bind(Button button, Action action) => button.Click += (_, _) => guard(() => Run(action));
        Bind(add, () => Begin(null)); Bind(edit, () => { if (Selected != null) Begin(Selected); });
        Bind(remove, Remove); Bind(save, Save); Bind(cancel, Cancel);
        selector.SelectionChanged += (_, _) => guard(Controls);
        foreach (var input in new[] { name, message })
        { input.GotKeyboardFocus += (_, _) => guard(() => typing.Acquire(input)); input.LostKeyboardFocus += (_, _) => guard(() => typing.Release(input)); }
    }
    private void Run(Action action) { try { action(); } catch (AdvisorTemplateException failure) { error(failure); } }
    internal void Open() { editingId = null; Refresh(store.Read(), null); }
    internal void Close() { editingId = null; Refresh(Array.Empty<AdvisorTextTemplate>(), null); }
    internal void Focus() { if (entries.Count == 0) add.Focus(); else selector.Focus(); }
    private void Controls()
    {
        var editing = editingId != null;
        selector.IsEnabled = add.IsEnabled = !editing;
        edit.IsEnabled = remove.IsEnabled = !editing && Selected != null;
        save.IsEnabled = cancel.IsEnabled = editing;
        editor.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Refresh(IReadOnlyList<AdvisorTextTemplate> value, string? selectedId)
    {
        entries = value; selector.Items.Clear();
        foreach (var entry in entries) selector.Items.Add(new ComboBoxItem { Content = entry.Name });
        var selected = entries.Select(e => e.Id).ToList().IndexOf(selectedId ?? "");
        selector.SelectedIndex = selected >= 0 ? selected : entries.Count > 0 ? 0 : -1;
        name.Text = message.Text = ""; Controls();
    }
    private void Begin(AdvisorTextTemplate? entry)
    {
        adding = entry == null; editingId = entry?.Id ?? "template-" + Guid.NewGuid().ToString("N");
        name.Text = entry?.Name ?? ""; message.Text = entry?.Text ?? "";
        save.Content = text()[adding ? "settings.register" : "settings.save_form"];
        changed(); Controls(); typing.Release(); name.Focus();
    }
    private void Save()
    {
        if (editingId == null) return;
        if (QuestionInputController.IsComposing) { composing(); return; }
        var entry = new AdvisorTextTemplate(editingId, name.Text ?? "", message.Text ?? "");
        var next = store.SaveEntry(entry, adding); editingId = null; Refresh(next, entry.Id); saved();
    }
    private void Cancel()
    {
        var selected = adding ? Selected?.Id : editingId;
        var next = store.Read(); editingId = null; Refresh(next, selected); changed();
    }
    private void Remove()
    {
        var entry = Selected; if (entry == null) return;
        confirmDelete(entry.Name, () => Run(() => { var next = store.Remove(entry.Id); Refresh(next, null); saved(); }));
    }
}
