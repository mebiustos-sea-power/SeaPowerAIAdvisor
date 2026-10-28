using System;
using System.Reflection;
using Noesis;
using UnityEngine;

namespace SeaPowerAIAdvisor.Shared;

internal static class GameTextInput
{
    private static readonly FieldInfo nativeComposition = CompositionField();
    internal static bool IsComposing => !string.IsNullOrEmpty(Input.compositionString) || !string.IsNullOrEmpty((string?)nativeComposition.GetValue(null));
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
