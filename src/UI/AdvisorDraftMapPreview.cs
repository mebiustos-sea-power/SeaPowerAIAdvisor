using System;
using System.Collections.Generic;
using System.Linq;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor;

// Own only the draft's markers; proposal previews and user drawings have separate lifetimes.
internal sealed class AdvisorDraftMapPreview : IDisposable
{
    private readonly IAdvisorMap? map;
    private IReadOnlyList<AdvisorLocation> shown = Array.Empty<AdvisorLocation>();
    private IDisposable? drawing;
    internal AdvisorDraftMapPreview(IAdvisorMap? map) => this.map = map;
    internal void Update(IReadOnlyList<AdvisorLocation> locations, bool visible)
    {
        if (!visible || map == null || locations.Count == 0) { Dispose(); return; }
        if (shown.SequenceEqual(locations)) return;
        Dispose();
        drawing = map.Draw(locations.Select(location => AdvisorMapDrawing.Point(location.Coordinate, location.Label)).ToArray());
        shown = locations.ToArray();
    }
    public void Dispose()
    {
        var previous = drawing; drawing = null; shown = Array.Empty<AdvisorLocation>();
        previous?.Dispose();
    }
}
