using System;
using System.Collections.Generic;
using System.Linq;
using Noesis;
using UnityEngine;

namespace SeaPowerAIAdvisor;

// The picker owns presentation and search; the existing draft editor owns badge insertion.
internal sealed class AdvisorUnitPicker
{
    private readonly Popup popup;
    private readonly FrameworkElement pane;
    private readonly TextBox search;
    private readonly ListBox list;
    private readonly TextBlock empty, count;
    private readonly Func<IReadOnlyList<(int Id, string Name)>> ownUnits;
    private readonly Func<AdvisorText> text;
    private readonly Action<int> choose;
    private readonly Action restoreFocus;
    private readonly Action<Action> guard;
    private readonly TypingScope typing;
    private readonly DeferredInputFocus focus;
    private readonly QuestionInput confirmation = new();
    private readonly UIElement anchor;
    private TextBox? inlineInput;
    private Action<int>? inlineChoose;
    private Action? inlineClosed;
    private string query = "";
    private int? pendingId;
    private IReadOnlyList<(int Id, string Name)> roster = Array.Empty<(int, string)>(), matches = Array.Empty<(int, string)>();
    private readonly HashSet<int> excluded = new();
    internal bool IsOpen => popup.IsOpen;
    internal bool IsInline => IsOpen && inlineInput != null;
    internal bool HasKeyboardFocus => pane.IsKeyboardFocusWithin;
    private static bool EnterDown => Input.GetKey(KeyCode.Return) || Input.GetKey(KeyCode.KeypadEnter);

    internal AdvisorUnitPicker(FrameworkElement content, Func<IReadOnlyList<(int Id, string Name)>> ownUnits, Func<AdvisorText> text,
        TypingScope typing, Action<int> choose, Action restoreFocus, Action<Action> guard)
    {
        this.ownUnits = ownUnits; this.text = text; this.typing = typing; this.choose = choose; this.restoreFocus = restoreFocus; this.guard = guard;
        focus = new DeferredInputFocus(typing);
        T Named<T>(string name) where T : class => content.FindName(name) as T ?? throw new InvalidOperationException("Missing unit picker control: " + name);
        popup = Named<Popup>("UnitPickerPopup"); pane = Named<FrameworkElement>("UnitPickerPane");
        anchor = Named<FrameworkElement>("InsertUnitButtons");
        search = Named<TextBox>("UnitPickerSearch"); list = Named<ListBox>("UnitPickerList");
        empty = Named<TextBlock>("UnitPickerEmpty"); count = Named<TextBlock>("UnitPickerCount");
        search.PreviewGotKeyboardFocus += (_, e) => { if (focus.Pending(search)) e.Handled = true; };
        pane.GotKeyboardFocus += (_, _) => guard(() => typing.Acquire(pane));
        pane.LostKeyboardFocus += (_, _) => guard(() =>
        {
            if (!pane.IsKeyboardFocusWithin) { ResetConfirmation(); typing.Release(pane); }
        });
        search.TextChanged += (_, _) => guard(() => { if (IsOpen && !IsInline) { query = search.Text ?? ""; ResetConfirmation(); Refresh(); } });
        pane.PreviewKeyDown += (_, e) => guard(() => HandleKey(e));
        popup.Opened += (_, _) => guard(() => { if (!IsInline) focus.Request(search); });
        popup.Closed += (_, _) => guard(Cleanup);
    }

    internal void Open(IEnumerable<int> insertedIds)
    {
        Close();
        search.Visibility = Visibility.Visible; popup.PlacementTarget = anchor; popup.StaysOpen = false;
        excluded.Clear(); excluded.UnionWith(insertedIds);
        ResetConfirmation();
        roster = ownUnits(); search.Text = query = "";
        popup.IsOpen = true; Refresh();
    }

    internal void ShowInline(TextBox input, string query, IEnumerable<int> insertedIds, Action<int> choose, Action closed)
    {
        if (!IsInline)
        {
            Close(); roster = ownUnits(); inlineInput = input;
            search.Visibility = Visibility.Collapsed; popup.PlacementTarget = input; popup.StaysOpen = true;
        }
        this.query = query; inlineChoose = choose; inlineClosed = closed;
        excluded.Clear(); excluded.UnionWith(insertedIds); ResetConfirmation();
        popup.IsOpen = true; Refresh();
    }

    private void Refresh()
    {
        matches = AdvisorUnitSearch.Find(roster, query, excluded);
        list.Items.Clear();
        foreach (var unit in matches)
        {
            var item = new ListBoxItem
            {
                Content = new TextBlock { Text = unit.Name, TextTrimming = TextTrimming.CharacterEllipsis },
                ToolTip = unit.Name, MinHeight = 28, Padding = new Thickness(6, 4, 6, 4),
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            item.PreviewMouseLeftButtonUp += (_, e) => guard(() => { e.Handled = true; Pick(unit.Id); });
            list.Items.Add(item);
        }
        list.SelectedIndex = matches.Count == 0 ? -1 : 0;
        list.Visibility = matches.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        empty.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        empty.Text = text()[string.IsNullOrWhiteSpace(query) ? "input.no_available_units" : "input.no_unit_matches"];
        count.Text = text().Format("input.unit_match_count", matches.Count);
    }

    private void Pick(int id)
    {
        if (GameTextInput.IsComposing) return;
        var current = ownUnits();
        if (!current.Any(u => u.Id == id)) { roster = current; Refresh(); return; }
        var selected = inlineChoose ?? choose;
        Close(); selected(id);
    }

    internal void HandleKey(KeyEventArgs e)
    {
        if (GameTextInput.IsComposing) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            var restore = inlineInput?.IsKeyboardFocused != true;
            Close(); if (restore) restoreFocus();
        }
        else if (e.Key == Key.Return || e.OriginalKey == Key.Return)
        {
            e.Handled = true;
            // Keep the popup and its typing scope until Unity no longer reports this Enter press.
            confirmation.Enter(Time.frameCount, false, e.IsRepeat, false, false);
            if (confirmation.Pending && pendingId == null && list.SelectedIndex >= 0 && list.SelectedIndex < matches.Count)
                pendingId = matches[list.SelectedIndex].Id;
        }
        else if ((e.Key == Key.Up || e.Key == Key.Down) && matches.Count > 0)
        {
            e.Handled = true;
            list.SelectedIndex = Math.Max(0, Math.Min(matches.Count - 1, list.SelectedIndex + (e.Key == Key.Down ? 1 : -1)));
            list.ScrollIntoView(list.Items[list.SelectedIndex]);
        }
    }

    internal void Tick()
    {
        if (!IsOpen) { focus.Cancel(); ResetConfirmation(); return; }
        if (inlineInput != null && !inlineInput.IsKeyboardFocused && !pane.IsKeyboardFocusWithin) { Close(); return; }
        focus.Tick();
        if (confirmation.TakeSubmission(Time.frameCount, pane.IsKeyboardFocusWithin || inlineInput?.IsKeyboardFocused == true, GameTextInput.IsComposing, false, EnterDown))
        {
            var id = pendingId; pendingId = null;
            if (id.HasValue) Pick(id.Value);
        }
        if (!confirmation.Pending) pendingId = null;
    }
    internal void Close()
    {
        if (IsOpen) popup.IsOpen = false;
    }
    private void Cleanup()
    {
        focus.Cancel(); ResetConfirmation(); typing.Release(pane);
        roster = matches = Array.Empty<(int, string)>(); excluded.Clear(); list.Items.Clear();
        var closed = inlineClosed;
        inlineInput = null; inlineChoose = null; inlineClosed = null; query = "";
        closed?.Invoke();
    }
    private void ResetConfirmation() { confirmation.Reset(EnterDown); pendingId = null; }
}
