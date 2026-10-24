using System;
using System.Collections.Generic;
using System.Linq;

namespace SeaPowerAIAdvisor.Api;

// Map data is independent of Noesis, the game and the operation that requested it.
public readonly struct AdvisorMapCoordinate
{
    public double Latitude { get; }
    public double Longitude { get; }
    public AdvisorMapCoordinate(double latitude, double longitude)
    {
        if (double.IsNaN(latitude) || double.IsInfinity(latitude) || Math.Abs(latitude) > 90 ||
            double.IsNaN(longitude) || double.IsInfinity(longitude) || Math.Abs(longitude) > 180)
            throw new ArgumentOutOfRangeException(nameof(latitude), "Invalid map coordinates.");
        Latitude = latitude; Longitude = longitude;
    }
}

public enum AdvisorMapDrawingKind { Point, Line, Circle }

public sealed class AdvisorMapDrawing
{
    public AdvisorMapDrawingKind Kind { get; }
    public IReadOnlyList<AdvisorMapCoordinate> Coordinates { get; }
    public double RadiusNauticalMiles { get; }
    public string Label { get; }
    public uint Color { get; }
    public float Width { get; }
    private AdvisorMapDrawing(AdvisorMapDrawingKind kind, IEnumerable<AdvisorMapCoordinate> coordinates,
        double radius, string label, uint color, float width)
    {
        if (float.IsNaN(width) || float.IsInfinity(width) || width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Kind = kind; Coordinates = Array.AsReadOnly(coordinates.ToArray()); RadiusNauticalMiles = radius;
        Label = label; Color = color; Width = width;
    }
    public static AdvisorMapDrawing Point(AdvisorMapCoordinate position, string label = "", uint color = 0xFFFFE060, float width = 2) =>
        new(AdvisorMapDrawingKind.Point, new[] { position }, 0, label, color, width);
    public static AdvisorMapDrawing Line(IEnumerable<AdvisorMapCoordinate> positions, uint color = 0xFFFFE060, float width = 2)
    {
        var line = new AdvisorMapDrawing(AdvisorMapDrawingKind.Line, positions, 0, "", color, width);
        if (line.Coordinates.Count < 2) throw new ArgumentException("A map line needs at least two coordinates.", nameof(positions));
        return line;
    }
    public static AdvisorMapDrawing Circle(AdvisorMapCoordinate center, double radiusNauticalMiles, uint color = 0xFFFFE060, float width = 2)
    {
        if (double.IsNaN(radiusNauticalMiles) || double.IsInfinity(radiusNauticalMiles) || radiusNauticalMiles <= 0 || radiusNauticalMiles > float.MaxValue / 1852d)
            throw new ArgumentOutOfRangeException(nameof(radiusNauticalMiles));
        return new AdvisorMapDrawing(AdvisorMapDrawingKind.Circle, new[] { center }, radiusNauticalMiles, "", color, width);
    }
}

public sealed class AdvisorMapPreview
{
    public static AdvisorMapPreview Empty { get; } = new(Array.Empty<AdvisorMapDrawing>());
    public IReadOnlyList<AdvisorMapDrawing> Drawings { get; }
    public AdvisorMapPreview(IEnumerable<AdvisorMapDrawing> drawings)
    { Drawings = Array.AsReadOnly(drawings.ToArray()); }
}

public interface IAdvisorMap
{
    void Center(AdvisorMapCoordinate location);
    // Each lease owns only its own drawings. Disposing it never clears other map content.
    IDisposable Draw(IReadOnlyList<AdvisorMapDrawing> drawings);
}
