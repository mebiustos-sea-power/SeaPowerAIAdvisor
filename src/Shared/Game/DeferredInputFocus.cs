using Noesis;
using UnityEngine;

namespace SeaPowerAIAdvisor.Shared;

// Automatic input focus follows mouse release, preserving the game's camera/button update.
internal sealed class DeferredInputFocus
{
    private readonly TypingScope typing;
    private TextBox? target;
    private UIElement? origin;
    private int frame;
    internal DeferredInputFocus(TypingScope typing) { this.typing = typing; }
    internal void Request(TextBox input)
    {
        Cancel(); typing.Release();
        if (input.View == null || !input.IsVisible) return;
        target = input; input.Keyboard.ClearFocus(); origin = input.Keyboard.FocusedElement; frame = Time.frameCount;
    }
    internal void Cancel() { target = null; origin = null; }
    internal bool Pending(TextBox input) => ReferenceEquals(target, input);
    internal void Tick()
    {
        var input = target; if (input == null) return;
        if (input.View == null || !input.IsVisible || !input.IsEnabled || input.Keyboard.FocusedElement != origin) { Cancel(); return; }
        if (Time.frameCount <= frame || !TypingScope.CanRestoreFocus()) return;
        Cancel(); input.Focus();
    }
}
