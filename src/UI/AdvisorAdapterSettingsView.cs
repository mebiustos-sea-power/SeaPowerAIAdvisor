using System;
using System.Linq;
using Noesis;

namespace SeaPowerAIAdvisor;

// Inspect registered connection types without opening connections, reading credentials or sending requests.
internal sealed class AdvisorAdapterSettingsView
{
    private readonly AdvisorServiceCatalog services;
    private readonly Func<AdvisorText> text;
    private readonly StackPanel entries;
    internal AdvisorAdapterSettingsView(StackPanel entries, AdvisorServiceCatalog services, Func<AdvisorText> text)
    { this.entries = entries; this.services = services; this.text = text; }
    internal void Open()
    {
        Close();
        var language = text();
        var available = services.Services.OrderBy(service => service.Id, StringComparer.Ordinal).ToArray();
        if (available.Length == 0)
            entries.Children.Add(new TextBlock { Text = language["extensions.adapters_empty"], TextWrapping = TextWrapping.Wrap });
        foreach (var service in available)
            entries.Children.Add(AdvisorExtensionCard.Create(service.Caption(language), service.Id, service.Version,
                service.Summary(language), language));
    }
    internal void Close() => entries.Children.Clear();
}
