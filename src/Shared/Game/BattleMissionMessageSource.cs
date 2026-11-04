using System;
using Newtonsoft.Json.Linq;
using Noesis;
using SeaPower;
using SeapowerUI;

namespace SeaPowerAIAdvisor.Shared;

// Observe the game's already-localized player popup, before any screen translation changes its UI.
internal static class BattleMissionMessageSource
{
    private static readonly BattleMissionMessageHistory Messages = new();
    internal static void Begin(SceneCreator mission) => Messages.Begin(mission);
    internal static void Clear() => Messages.Clear();
    internal static void Publish(MissionPopupData message, UIElement context, DateTime? publishedAt)
    {
        // Editor previews use MissionEditorView; reopening an alert reuses its existing popup.
        if (context is not MainGameView || !ReferenceEquals(context, MainGameView.Instance)) return;
        Messages.Publish(SceneCreator.Instance, message.Header, message.Body,
            publishedAt ?? RichMissionMessages.GetRuntimeMissionDateTime());
    }
    internal static JArray Capture() => Messages.Capture(SceneCreator.Instance);
}
