using System;
using System.Reflection;
using Noesis;
using UnityEngine;

namespace SeaPowerAIAdvisor;

// Adapts the game's Noesis/Unity event order to the independent input policy.
internal sealed class QuestionInputController
{
    private static readonly FieldInfo nativeComposition = CompositionField();
    private readonly QuestionInput input = new();
    private readonly TextBox question;
    private readonly Func<bool> busy;
    private readonly Action submit;

    internal QuestionInputController(TextBox question, Func<bool> busy, Action submit, Action<Action> guard)
    {
        this.question = question;
        this.busy = busy;
        this.submit = submit;
        question.PreviewKeyDown += (_, e) => guard(() => KeyDown(e));
        question.PreviewTextInput += (_, e) => guard(() =>
        {
            // NoesisView sends Char separately from KeyDown, including Return.
            // Consume only a standalone Enter character; pasted multiline text
            // and ordinary committed characters retain the native editing path.
            if (e.Text == "\r" || e.Text == "\n" || e.Text == "\r\n") e.Handled = true;
            else input.TextEntered(e.Text);
        });
        question.GotKeyboardFocus += (_, _) => guard(Reset);
        question.LostKeyboardFocus += (_, _) => guard(Reset);
    }

    internal static bool IsComposing =>
        !string.IsNullOrEmpty(Input.compositionString) ||
        !string.IsNullOrEmpty((string?)nativeComposition.GetValue(null));

    private static bool EnterDown => Input.GetKey(KeyCode.Return) || Input.GetKey(KeyCode.KeypadEnter);

    internal void Tick()
    {
        if (input.TakeSubmission(Time.frameCount, question.IsKeyboardFocused, IsComposing, busy(), EnterDown))
            submit();
    }

    internal void Reset() => input.Reset(EnterDown);

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
