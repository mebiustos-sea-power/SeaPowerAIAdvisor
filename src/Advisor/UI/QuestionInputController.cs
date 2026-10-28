using System;
using System.Reflection;
using Noesis;
using UnityEngine;

namespace SeaPowerAIAdvisor;

// Adapts Noesis/Unity event order to the input and deferred focus policies.
internal sealed class QuestionInputController
{
    private static readonly FieldInfo nativeComposition = CompositionField();
    private readonly QuestionInput input = new();
    private readonly QuestionFocus focus = new();
    private readonly TypingScope typing;
    private UIElement? focusOrigin;
    private readonly TextBox question;
    private readonly Func<bool> busy;
    private readonly Action submit;

    internal QuestionInputController(TextBox question, TypingScope typing, Func<bool> busy, Action submit, Action<Action> guard)
    {
        this.question = question; this.typing = typing;
        this.busy = busy;
        this.submit = submit;
        var menu = question.ContextMenu ?? throw new InvalidOperationException("The question context menu is missing.");
        foreach (MenuItem item in menu.Items) item.CommandTarget = question;
        menu.Closed += (_, _) => guard(RequestFocus);
        question.PreviewGotKeyboardFocus += (_, e) => guard(() =>
        {
            // Native focus restoration must not bypass a pending input-release wait.
            if (focus.Pending) e.Handled = true;
        });
        question.PreviewKeyDown += (_, e) => guard(() => KeyDown(e));
        question.PreviewTextInput += (_, e) => guard(() =>
        {
            // NoesisView sends Char separately from KeyDown, including Return.
            // Consume only a standalone Enter character; pasted multiline text
            // and ordinary committed characters retain the native editing path.
            if (e.Text == "\r" || e.Text == "\n" || e.Text == "\r\n") e.Handled = true;
            else input.TextEntered(e.Text);
        });
        question.GotKeyboardFocus += (_, _) => guard(() => { Reset(); typing.Acquire(question); });
        question.LostKeyboardFocus += (_, _) => guard(() => { Reset(); typing.Release(question); });
    }

    internal static bool IsComposing =>
        !string.IsNullOrEmpty(Input.compositionString) ||
        !string.IsNullOrEmpty((string?)nativeComposition.GetValue(null));

    private static bool EnterDown => Input.GetKey(KeyCode.Return) || Input.GetKey(KeyCode.KeypadEnter);

    internal void RequestFocus()
    {
        Reset();
        // Allow the game's input update to consume release before entering typing mode again.
        typing.Release(question);
        // Hidden or detached inputs have no keyboard and do not need focus restoration.
        if (question.View == null || !question.IsVisible) return;
        var keyboard = question.Keyboard ?? throw new InvalidOperationException("The visible question input has no keyboard.");
        keyboard.ClearFocus();
        focusOrigin = keyboard.FocusedElement;
        focus.Request(Time.frameCount);
    }

    internal void Tick()
    {
        if (focus.Pending)
        {
            var eligible = question.IsVisible && question.IsEnabled && !question.IsReadOnly &&
                question.Keyboard.FocusedElement == focusOrigin;
            if (focus.TakeFocus(Time.frameCount, eligible, eligible && TypingScope.CanRestoreFocus())) question.Focus();
            if (!focus.Pending) focusOrigin = null;
        }
        if (input.TakeSubmission(Time.frameCount, question.IsKeyboardFocused, IsComposing, busy(), EnterDown))
            submit();
    }

    internal void Reset()
    {
        input.Reset(EnterDown);
        focus.Cancel(); focusOrigin = null;
    }

    private void KeyDown(KeyEventArgs e)
    {
        if (e.Key != Key.Return && e.OriginalKey != Key.Return) return;
        e.Handled = true;
        var action = input.Enter(Time.frameCount, (question.Keyboard.Modifiers & ModifierKeys.Shift) != 0,
            e.IsRepeat, busy(), IsComposing);
        if (action != QuestionKeyAction.InsertNewline) return;
        var caret = question.SelectionStart + 1;
        question.SelectedText = "\n";
        question.SelectionStart = caret;
        question.SelectionLength = 0;
    }

    private static FieldInfo CompositionField()
    {
        // Unity's live composition can already be empty on confirmation while
        // Noesis still holds its previous value until IME.Update. Read both;
        // never alter the game's IME or silently discard a changed contract.
        var ime = typeof(TextBox).Assembly.GetType("NoesisUnity+IME", true)!;
        var field = ime.GetField("compositionString", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(ime.FullName, "compositionString");
        if (field.FieldType != typeof(string))
            throw new InvalidOperationException("The Noesis IME composition contract changed.");
        return field;
    }
}
