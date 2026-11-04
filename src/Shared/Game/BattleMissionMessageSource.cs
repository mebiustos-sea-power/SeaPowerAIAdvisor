using System;
using Newtonsoft.Json.Linq;
using Noesis;
using SeaPower;
using SeapowerUI;

namespace SeaPowerAIAdvisor.Shared;

// Observe the game's already-localized player popup, before any screen translation changes its UI.
internal static class BattleMissionMessageSource
{
    internal static BattleMissionMessageHistory History { get; } = new();
    internal static void Clear() => History.Clear();
    internal static void Publish(MissionPopupData message, UIElement context, DateTime? publishedAt)
    {
        // Editor previews use MissionEditorView; reopening an alert reuses its existing popup.
        if (context is not MainGameView || !ReferenceEquals(context, MainGameView.Instance)) return;
        History.Publish(SceneCreator.Instance, message.Header, message.Body,
            publishedAt ?? RichMissionMessages.GetRuntimeMissionDateTime());
    }
    internal static JArray Capture() => History.Capture(SceneCreator.Instance);
}
