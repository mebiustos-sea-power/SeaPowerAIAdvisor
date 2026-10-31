using System;
using System.Collections.Generic;
using Noesis;

namespace SeaPowerAIAdvisor;

// Connects text editing to the shared picker without changing game selection or sending a question.
internal sealed class AdvisorUnitMentionController
{
    private readonly TextBox question;
    private readonly AdvisorUnitPicker picker;
    private readonly Func<bool> enabled;
    private readonly Func<IEnumerable<int>> insertedIds;
    private readonly Action<int> insert;
    private (string Text, int Caret, int Selection)? active, dismissed;
    private bool dirty = true, wasComposing;

    internal AdvisorUnitMentionController(FrameworkElement content, TextBox question, AdvisorUnitPicker picker,
        Func<bool> enabled, Func<IEnumerable<int>> insertedIds, Action<int> insert, Action<Action> guard)
    {
        this.question = question; this.picker = picker; this.enabled = enabled; this.insertedIds = insertedIds; this.insert = insert;
        question.TextChanged += (_, _) => dirty = true;
        question.SelectionChanged += (_, _) => dirty = true;
        question.GotKeyboardFocus += (_, _) => dirty = true;
        question.LostKeyboardFocus += (_, _) => dirty = true;
        // Tunnel before QuestionInputController, so candidate confirmation cannot queue a consultation.
        content.PreviewKeyDown += (_, e) => guard(() =>
        {
            if (!question.IsKeyboardFocused || GameTextInput.IsComposing || !enabled()) return;
            if ((e.Key == Key.Return || e.OriginalKey == Key.Return) &&
                (question.Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
            if (e.Key != Key.Return && e.OriginalKey != Key.Return && e.Key != Key.Up && e.Key != Key.Down && e.Key != Key.Escape) return;
            Refresh();
            if (picker.IsInline) picker.HandleKey(e);
        });
    }

    internal void Tick()
    {
        var composing = GameTextInput.IsComposing;
        if (!enabled()) { if (picker.IsInline) picker.Close(); dirty = true; return; }
        if (composing) { wasComposing = true; return; }
        if (wasComposing) { wasComposing = false; dirty = true; }
        if (dirty) Refresh();
    }

    private void Refresh()
    {
        dirty = false;
        if (!question.IsKeyboardFocused)
        {
            if (picker.IsInline && !picker.HasKeyboardFocus) picker.Close();
            return;
        }
        var state = (Text: question.Text ?? "", Caret: question.SelectionStart, Selection: question.SelectionLength);
        if (state == active && picker.IsInline) return;
        var token = AdvisorMentionQuery.At(state.Text, state.Caret, state.Selection);
        if (!token.HasValue || state == dismissed)
        { if (picker.IsInline) picker.Close(); return; }
        if (picker.IsOpen && !picker.IsInline) return;
        active = state;
        picker.ShowInline(question, token.Value.Query, insertedIds(), id =>
        {
            if (!enabled() || question.Text != state.Text || question.SelectionStart != state.Caret || question.SelectionLength != state.Selection) return;
            question.Text = state.Text.Remove(token.Value.Start, token.Value.Length);
            question.SelectionStart = token.Value.Start; question.SelectionLength = 0;
            insert(id);
        }, () => { dismissed = active; active = null; });
    }
}
