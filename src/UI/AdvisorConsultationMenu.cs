using System;
using Noesis;

namespace SeaPowerAIAdvisor;

// Menu sections share the history's display area; choosing text never starts a consultation.
internal sealed class AdvisorConsultationMenu
{
    private readonly FrameworkElement content, pane, history;
    private readonly StackPanel groups;
    private readonly ToggleButton launcher;
    private readonly AdvisorTextTemplateStore templates;
    private readonly Func<AdvisorText> text;
    private readonly Action<AdvisorTextTemplate> choose;
    private readonly Action<Action> guard;
    private readonly Action<string> warn;
    internal AdvisorConsultationMenu(FrameworkElement content, AdvisorTextTemplateStore templates, Func<AdvisorText> text,
        Action<AdvisorTextTemplate> choose, Action<Action> guard, Action<string> warn)
    {
        this.content = content; this.templates = templates; this.text = text; this.choose = choose; this.guard = guard; this.warn = warn;
        T Named<T>(string id) where T : class => content.FindName(id) as T ?? throw new InvalidOperationException("Missing consultation menu control: " + id);
        pane = Named<FrameworkElement>("OtherConsultationsPane"); history = Named<FrameworkElement>("ConversationHistory");
        groups = Named<StackPanel>("OtherConsultationGroups");
        launcher = Named<ToggleButton>("OtherConsultations");
    }
    internal bool IsOpen => pane.Visibility == Visibility.Visible;
    internal void Open()
    {
        groups.Children.Clear();
        groups.Children.Add(new TextBlock { Text = text()["templates.category"], FontSize = 14, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10) });
        history.Visibility = Visibility.Collapsed; pane.Visibility = Visibility.Visible; launcher.IsChecked = true;
        try
        {
            var entries = templates.Read();
            if (entries.Count == 0) groups.Children.Add(new TextBlock { Text = text()["templates.empty"], TextWrapping = TextWrapping.Wrap });
            foreach (var entry in entries)
            {
                var button = new Button { Content = new TextBlock { Text = entry.Name, TextWrapping = TextWrapping.Wrap },
                    MinHeight = 32, HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 6) };
                button.Click += (_, _) => guard(() => choose(entry));
                groups.Children.Add(button);
            }
        }
        catch (AdvisorTemplateException error)
        {
            groups.Children.Add(new TextBlock { Text = text()["settings.error." + error.Code] + text().Format("settings.error_suffix", error.Code),
                TextWrapping = TextWrapping.Wrap, Foreground = (Brush)content.FindResource("AdvisorErrorBrush") });
            warn("Advisor text templates failed: " + error.Code);
        }
    }
    internal void Close() { pane.Visibility = Visibility.Collapsed; history.Visibility = Visibility.Visible; launcher.IsChecked = false; }
}
