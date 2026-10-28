using System;
using System.Collections.Generic;
using System.Linq;
using Noesis;

namespace SeaPowerAIAdvisor.Shared;

// Cards preserve the supplied connection order and report identity; editing and persistence belong to the API page.
internal sealed class AiConnectionListView
{
    private readonly StackPanel host;
    private readonly Func<string, string> text;
    private readonly Func<string> language;
    private readonly Action<string> open;
    private readonly Action<Action> guard;

    internal AiConnectionListView(StackPanel host, Func<string, string> text, Func<string> language,
        Action<string> open, Action<Action> guard)
    { this.host = host; this.text = text; this.language = language; this.open = open; this.guard = guard; }

    internal void Show(IReadOnlyList<AiConnectionProfile> orderedEntries, IReadOnlyList<IAiConnectionProvider> choices, Func<string, string?> role)
    {
        host.Children.Clear();
        if (orderedEntries.Count == 0)
            host.Children.Add(new TextBlock { Text = text("connection.empty"), TextWrapping = TextWrapping.Wrap });
        foreach (var entry in orderedEntries)
        {
            var service = choices.SingleOrDefault(p => p.Id == entry.Provider);
            var description = service == null ? entry.Provider + " / " + text("connection.provider_unavailable") : service.Caption(text, language());
            var model = service?.Model(entry) ?? "";
            if (model.Length > 0) description += " / " + model;
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.Children.Add(new TextBlock { Text = entry.Name, FontSize = 14, FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap });
            var roleText = role(entry.Id);
            if (roleText != null)
            {
                var label = new TextBlock { Text = roleText, FontSize = 12, FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                Grid.SetColumn(label, 1); content.Children.Add(label);
            }
            var detail = new TextBlock { Text = description, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 0) };
            Grid.SetRow(detail, 1); Grid.SetColumnSpan(detail, 2); content.Children.Add(detail);
            var card = new Button { Content = content, MinHeight = 62, Padding = new Thickness(10, 8, 10, 8),
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 8) };
            card.Click += (_, _) => guard(() => open(entry.Id));
            host.Children.Add(card);
        }
    }
}
