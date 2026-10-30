using System;
using System.Diagnostics;
using Noesis;

namespace SeaPowerAIAdvisor.Shared;

// UI feedback shares colors; brief notices use the settings confirmation fade and lifetime.
internal sealed class UiNotice
{
    private readonly TextBlock control;
    private readonly FrameworkElement resources;
    private readonly Stopwatch brief = new();
    internal UiNotice(TextBlock control, FrameworkElement resources)
    { this.control = control; this.resources = resources; }
    internal void Show(string message, bool error = false)
    {
        brief.Reset(); control.Opacity = 1; control.Text = message;
        control.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        control.Foreground = (Brush)resources.FindResource(error ? "AdvisorErrorBrush" : "AdvisorNoticeBrush");
    }
    internal void ShowBriefly(string message)
    {
        Show(message); control.Opacity = 0; brief.Restart();
    }
    internal void Tick()
    {
        if (!brief.IsRunning) return;
        var elapsed = brief.Elapsed.TotalSeconds;
        if (elapsed >= 3) Show("");
        else control.Opacity = (float)Math.Min(1, elapsed / 0.2);
    }
}
