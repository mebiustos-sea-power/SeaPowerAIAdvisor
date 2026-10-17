using System;
using System.Linq;
using Noesis;

namespace SeaPowerAIAdvisor;

// Edits registered connections and role assignments; persistence and network tests belong to the window.
internal sealed class AdvisorApiSettingsView
{
    private readonly ComboBox main, sub, selector;
    private readonly TextBox name, model;
    private readonly PasswordBox key;
    private readonly Button add, remove;
    private readonly Func<AdvisorText> text;
    private readonly Action changed;
    private readonly TypingScope typing;
    private AdvisorSettingsDraft? draft;
    private string[] ids = Array.Empty<string>();
    private string? selectedId;
    private bool updating, busy;
    internal string? SelectedId => selectedId;
    internal string SelectedName => Caption(draft?.Find(selectedId));

    internal AdvisorApiSettingsView(FrameworkElement content, Func<AdvisorText> text, Action<Action> guard, TypingScope typing, Action changed)
    {
        this.text = text; this.typing = typing; this.changed = changed;
        T Named<T>(string id) where T : class => content.FindName(id) as T
            ?? throw new InvalidOperationException("The API settings control is missing: " + id);
        main = Named<ComboBox>("MainApiConfiguration"); sub = Named<ComboBox>("SubApiConfiguration");
        selector = Named<ComboBox>("ApiConfigurationSelector");
        name = Named<TextBox>("ApiConfigurationName"); model = Named<TextBox>("ModelName"); key = Named<PasswordBox>("ApiKey");
        add = Named<Button>("AddApiConfiguration"); remove = Named<Button>("RemoveApiConfiguration");
        name.TextChanged += (_, _) => guard(Rename);
        model.TextChanged += (_, _) => guard(NotifyChanged);
        key.PasswordChanged += (_, _) => guard(NotifyChanged);
        main.SelectionChanged += (_, _) => guard(() => Assign(false));
        sub.SelectionChanged += (_, _) => guard(() => Assign(true));
        selector.SelectionChanged += (_, _) => guard(Select);
        add.Click += (_, _) => guard(Add); remove.Click += (_, _) => guard(Remove);
        foreach (var input in new[] { name, model })
        {
            input.GotKeyboardFocus += (_, _) => guard(() => typing.Acquire(input));
            input.LostKeyboardFocus += (_, _) => guard(() => typing.Release(input));
        }
        key.GotKeyboardFocus += (_, _) => guard(() => typing.Acquire(key));
        key.LostKeyboardFocus += (_, _) => guard(() => typing.Release(key));
    }

    internal void Reset(AdvisorSettingsDraft? value)
    { draft = value; Refresh(value?.Configurations.FirstOrDefault()?.Id); }

    internal void Read()
    {
        var entry = draft?.Find(selectedId);
        if (entry == null) return;
        entry.Name = name.Text ?? ""; entry.ApiKey = key.Password; entry.Model = model.Text ?? "";
    }

    internal void SetBusy(bool value)
    {
        busy = value;
        main.IsEnabled = sub.IsEnabled = selector.IsEnabled = add.IsEnabled = draft != null && !busy;
        name.IsEnabled = key.IsEnabled = model.IsEnabled = remove.IsEnabled = draft?.Find(selectedId) != null && !busy;
    }

    internal bool Focus()
    { if (!main.IsEnabled) return false; main.Focus(); return true; }

    private string Caption(AdvisorApiConfigurationDraft? entry) => string.IsNullOrWhiteSpace(entry?.Name)
        ? text()["connection.unnamed_configuration"] : entry!.Name;

    private void Refresh(string? selection)
    {
        updating = true;
        try
        {
            ids = draft?.Configurations.Select(c => c.Id).ToArray() ?? Array.Empty<string>();
            main.Items.Clear(); sub.Items.Clear(); selector.Items.Clear();
            main.Items.Add(new ComboBoxItem { Content = text()["connection.choose_configuration"] });
            sub.Items.Add(new ComboBoxItem { Content = text()["connection.no_sub"] });
            foreach (var id in ids)
            {
                var caption = Caption(draft!.Find(id));
                foreach (var control in new[] { main, sub, selector }) control.Items.Add(new ComboBoxItem { Content = caption });
            }
            main.SelectedIndex = Array.IndexOf(ids, draft?.MainId) + 1;
            sub.SelectedIndex = Array.IndexOf(ids, draft?.SubId) + 1;
            selectedId = selection;
            selector.SelectedIndex = Array.IndexOf(ids, selectedId);
            Display();
        }
        finally { updating = false; }
        SetBusy(busy);
    }

    private void Display()
    {
        var entry = draft?.Find(selectedId);
        name.Text = entry?.Name ?? ""; key.Password = entry?.ApiKey ?? ""; model.Text = entry?.Model ?? "";
    }

    private void Select()
    {
        if (updating || busy || draft == null) return;
        Read();
        selectedId = selector.SelectedIndex < 0 ? null : ids[selector.SelectedIndex];
        updating = true;
        try { Display(); } finally { updating = false; }
        SetBusy(false); changed();
    }

    private void Assign(bool fallback)
    {
        if (updating || busy || draft == null) return;
        var index = (fallback ? sub : main).SelectedIndex;
        var id = index <= 0 ? null : ids[index - 1];
        if (fallback) draft.SubId = id; else draft.MainId = id;
        SetBusy(false);
    }

    private void Rename()
    {
        if (updating || busy || draft?.Find(selectedId) is not AdvisorApiConfigurationDraft entry) return;
        entry.Name = name.Text ?? "";
        var index = Array.IndexOf(ids, selectedId);
        var caption = Caption(entry);
        ((ComboBoxItem)selector.Items[index]).Content = caption;
        ((ComboBoxItem)main.Items[index + 1]).Content = caption;
        ((ComboBoxItem)sub.Items[index + 1]).Content = caption;
        changed();
    }

    private void NotifyChanged()
    { if (!updating && !busy) changed(); }

    private void Add()
    {
        if (busy || draft == null) return;
        Read(); var entry = draft.Add(text()["connection.configuration_name_pattern"]);
        Refresh(entry.Id); changed(); typing.Release(); name.Focus();
    }

    private void Remove()
    {
        if (busy || draft == null || selectedId == null) return;
        Read(); draft.Remove(selectedId);
        Refresh(draft.Configurations.FirstOrDefault()?.Id); changed(); typing.Release(); selector.Focus();
    }
}
