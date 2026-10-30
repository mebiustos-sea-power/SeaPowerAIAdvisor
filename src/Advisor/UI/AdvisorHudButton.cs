using System;
using Noesis;

namespace SeaPowerAIAdvisor;

// Advisor-specific consultation indicators use the same HUD tile as other MOD features.
internal sealed class AdvisorHudButton : IDisposable
{
    private readonly HudButton button;
    private readonly Brush consultingBrush;
    private readonly Brush unreadBrush = new SolidColorBrush(Color.FromArgb(255, 128, 224, 96));
    internal ToggleButton Control => button.Control;

    internal AdvisorHudButton(Grid grid, ToggleButton reference, string tooltip)
    {
        button = new HudButton(grid, reference, "AI", tooltip);
        consultingBrush = button.Indicator.Fill;
    }
    internal void SetStatus(bool consulting, bool unread)
    {
        button.Indicator.Fill = consulting ? consultingBrush : unreadBrush;
        button.Indicator.Visibility = Control.IsChecked != true && (consulting || unread) ? Visibility.Visible : Visibility.Collapsed;
    }
    public void Dispose() => button.Dispose();
}
