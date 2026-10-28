using Noesis;
using SeaPower;
using UnityEngine;

namespace SeaPowerAIAdvisor;

internal sealed class TypingScope
{
    private UIElement? owned;

    internal static bool CanRestoreFocus()
    {
        if (Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2)) return false;
        var input = InputHandler.Instance;
        if (input.typingActive || input.menuOpen || input.consoleOpen || input.RebindMode.Value) return false;
        if (!CameraManager.InstanceExists(false)) return true;
        var camera = CameraManager.Instance;
        return !camera.LeftMousePressed && !camera.RightMousePressed;
    }

    internal void Acquire(UIElement element)
    {
        var input = InputHandler.Instance;
        owned = element;
        input.typingActive = true;
        input.typingElement = element;
    }

    internal void Release(UIElement element)
    {
        if (owned == element) Release();
    }

    internal void Release()
    {
        if (owned == null) return;
        // Another game input can take focus before our LostKeyboardFocus callback.
        // Release only the element this window owns.
        if (InputHandler.InstanceExists(false))
        {
            var input = InputHandler.Instance;
            if (input.typingElement == owned)
            {
                input.typingElement = null;
                input.typingActive = false;
            }
        }
        owned = null;
    }
}
