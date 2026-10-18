using System;
using System.Collections.Generic;

namespace SeaPowerAIAdvisor;

// Internal composition boundary; SDK registration and persisted provider settings are separate work.
internal sealed class AdvisorBackendRegistry
{
    private readonly object sync = new();
    private readonly Dictionary<string, Func<AdvisorBackendSetup>> factories = new(StringComparer.Ordinal);
    private string? selected;
    internal string? SelectedId { get { lock (sync) return selected; } }

    internal void Register(string id, Func<AdvisorBackendSetup> prepare)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A backend ID is required.", nameof(id));
        if (prepare == null) throw new ArgumentNullException(nameof(prepare));
        lock (sync)
        {
            if (factories.ContainsKey(id)) throw new InvalidOperationException("The adviser backend is already registered: " + id);
            factories.Add(id, prepare);
        }
    }

    internal void Select(string id)
    {
        lock (sync)
        {
            if (!factories.ContainsKey(id)) throw new KeyNotFoundException("The adviser backend is not registered: " + id);
            selected = id;
        }
    }

    internal AdvisorBackendSetup Prepare()
    {
        Func<AdvisorBackendSetup> prepare;
        lock (sync)
        {
            if (selected == null) throw new InvalidOperationException("No adviser backend is selected.");
            prepare = factories[selected];
        }
        // Capture a configuration for this consultation; registration changes cannot replace it mid-flight.
        return prepare() ?? throw new InvalidOperationException("The adviser backend returned no configuration.");
    }
}
