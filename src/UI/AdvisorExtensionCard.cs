using Noesis;

namespace SeaPowerAIAdvisor;

// Shared presentation for registered capabilities; execution and registration stay with their owners.
internal static class AdvisorExtensionCard
{
    internal static Border Create(string name, string identifier, string? version, string summary, AdvisorText text, Button? action = null)
    {
        var heading = new Grid();
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        if (action != null) { Grid.SetColumn(action, 1); heading.Children.Add(action); }
        var card = new StackPanel();
        card.Children.Add(heading);
        card.Children.Add(new TextBlock { Text = identifier, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75f, Margin = new Thickness(0, 3, 0, 5) });
        if (version != null)
            card.Children.Add(new TextBlock { Text = text.Format("extensions.version", version), FontSize = 11,
                TextWrapping = TextWrapping.Wrap, Opacity = 0.75f, Margin = new Thickness(0, 0, 0, 5) });
        if (!string.IsNullOrWhiteSpace(summary))
            card.Children.Add(new TextBlock { Text = summary, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        return new Border { Child = card, BorderBrush = new SolidColorBrush(Color.FromArgb(255, 85, 96, 107)),
            BorderThickness = new Thickness(1), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 12) };
    }
}
