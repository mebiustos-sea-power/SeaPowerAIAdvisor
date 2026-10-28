using System.Globalization;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor;

// A draft location is a fixed geographic reference, independent of unit identities.
internal sealed class AdvisorLocation
{
    internal int Index { get; }
    internal string Label { get; }
    internal AdvisorMapCoordinate Coordinate { get; }
    internal string Destination => Coordinate.Latitude.ToString("R", CultureInfo.InvariantCulture) + "," +
        Coordinate.Longitude.ToString("R", CultureInfo.InvariantCulture);
    internal string Reference => Label + " (latitude: " + Coordinate.Latitude.ToString("R", CultureInfo.InvariantCulture) +
        ", longitude: " + Coordinate.Longitude.ToString("R", CultureInfo.InvariantCulture) + ")";
    internal AdvisorLocation(int index, string label, AdvisorMapCoordinate coordinate) { Index = index; Label = label; Coordinate = coordinate; }
}
