using System;
using System.Collections.Generic;
using System.Linq;
using Noesis;

namespace SeaPowerAIAdvisor;

// Cards present saved connections and report identity; editing and persistence belong to the API page.
internal sealed class AdvisorApiConnectionListView
{
    private readonly StackPanel host;
    private readonly AdvisorServiceCatalog services;
    private readonly Func<AdvisorText> text;
    private readonly Action<string> open;
    private readonly Action<Action> guard;

    internal AdvisorApiConnectionListView(StackPanel host, AdvisorServiceCatalog services, Func<AdvisorText> text,
        Action<string> open, Action<Action> guard)
    { this.host = host; this.services = services; this.text = text; this.open = open; this.guard = guard; }

    internal void Show(IReadOnlyList<AdvisorApiConfiguration> entries, string? mainId, string? subId)
    {
        host.Children.Clear();
        if (entries.Count == 0)
            host.Children.Add(new TextBlock { Text = text()["connection.empty"], TextWrapping = TextWrapping.Wrap });
        foreach (var entry in entries.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var service = services.Find(entry.Provider);
            var description = service == null ? entry.Provider + " / " + text()["connection.provider_unavailable"] : service.Caption(text());
            var model = service?.Model(entry) ?? "";
            if (model.Length > 0) description += " / " + model;
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.Children.Add(new TextBlock { Text = entry.Name, FontSize = 14, FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap });
            var role = entry.Id == mainId ? "connection.card_main" : entry.Id == subId ? "connection.card_sub" : null;
            if (role != null)
            {
                var label = new TextBlock { Text = text()[role], FontSize = 12, FontWeight = FontWeights.Bold,
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
