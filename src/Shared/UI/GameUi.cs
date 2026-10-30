using Noesis;
using SeapowerUI;

namespace SeaPowerAIAdvisor.Shared;

internal static class GameUi
{
    internal static T? FindVisual<T>(DependencyObject node) where T : DependencyObject
    {
        if (node is T match) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        { var found = FindVisual<T>(VisualTreeHelper.GetChild(node, i)); if (found != null) return found; }
        return null;
    }
    internal static WindowHost? FindWindowHost(DependencyObject node)
    {
        if (node is WindowHost host) return host;
        if (node is FrameworkElement root)
        {
            if (root.FindName("WindowHost") is WindowHost named) return named;
            // Native window hosts may be attached behaviors rather than visual children.
            foreach (var behavior in Noesis.Interactivity.Interaction.GetBehaviors(root)) if (behavior is WindowHost attached) return attached;
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        { var found = FindWindowHost(VisualTreeHelper.GetChild(node, i)); if (found != null) return found; }
        return null;
    }
}
