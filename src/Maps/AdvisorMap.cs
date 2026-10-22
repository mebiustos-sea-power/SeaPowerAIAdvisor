using System;
using System.Collections.Generic;
using System.Linq;

namespace SeaPowerAIAdvisor;

// Map data is independent of Noesis, the game and the operation that requested it.
internal readonly struct AdvisorMapCoordinate
{
    internal double Latitude { get; }
    internal double Longitude { get; }
    internal AdvisorMapCoordinate(double latitude, double longitude)
    {
        if (double.IsNaN(latitude) || double.IsInfinity(latitude) || Math.Abs(latitude) > 90 ||
            double.IsNaN(longitude) || double.IsInfinity(longitude) || Math.Abs(longitude) > 180)
            throw new ArgumentOutOfRangeException(nameof(latitude), "Invalid map coordinates.");
        Latitude = latitude; Longitude = longitude;
    }
}

internal enum AdvisorMapDrawingKind { Point, Line, Circle }

internal sealed class AdvisorMapDrawing
{
    internal AdvisorMapDrawingKind Kind { get; }
    internal IReadOnlyList<AdvisorMapCoordinate> Coordinates { get; }
    internal double RadiusNauticalMiles { get; }
    internal string Label { get; }
    internal uint Color { get; }
    internal float Width { get; }
    private AdvisorMapDrawing(AdvisorMapDrawingKind kind, IEnumerable<AdvisorMapCoordinate> coordinates,
        double radius, string label, uint color, float width)
    {
        if (float.IsNaN(width) || float.IsInfinity(width) || width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Kind = kind; Coordinates = Array.AsReadOnly(coordinates.ToArray()); RadiusNauticalMiles = radius;
        Label = label; Color = color; Width = width;
    }
    internal static AdvisorMapDrawing Point(AdvisorMapCoordinate position, string label = "", uint color = 0xFFFFE060, float width = 2) =>
        new(AdvisorMapDrawingKind.Point, new[] { position }, 0, label, color, width);
    internal static AdvisorMapDrawing Line(IEnumerable<AdvisorMapCoordinate> positions, uint color = 0xFFFFE060, float width = 2)
    {
        var line = new AdvisorMapDrawing(AdvisorMapDrawingKind.Line, positions, 0, "", color, width);
        if (line.Coordinates.Count < 2) throw new ArgumentException("A map line needs at least two coordinates.", nameof(positions));
        return line;
    }
    internal static AdvisorMapDrawing Circle(AdvisorMapCoordinate center, double radiusNauticalMiles, uint color = 0xFFFFE060, float width = 2)
    {
        if (double.IsNaN(radiusNauticalMiles) || double.IsInfinity(radiusNauticalMiles) || radiusNauticalMiles <= 0 || radiusNauticalMiles > float.MaxValue / 1852d)
            throw new ArgumentOutOfRangeException(nameof(radiusNauticalMiles));
        return new AdvisorMapDrawing(AdvisorMapDrawingKind.Circle, new[] { center }, radiusNauticalMiles, "", color, width);
    }
}

internal sealed class AdvisorMapPreview
{
    internal static AdvisorMapPreview Empty { get; } = new(Array.Empty<AdvisorMapDrawing>());
    internal IReadOnlyList<AdvisorMapDrawing> Drawings { get; }
    internal AdvisorMapCoordinate? Center { get; }
    internal AdvisorMapPreview(IEnumerable<AdvisorMapDrawing> drawings, AdvisorMapCoordinate? center = null)
    { Drawings = Array.AsReadOnly(drawings.ToArray()); Center = center; }
}

internal interface IAdvisorMap
{
    void Center(AdvisorMapCoordinate location);
    // Each lease owns only its own drawings. Disposing it never clears other map content.
    IDisposable Draw(IReadOnlyList<AdvisorMapDrawing> drawings);
}
