using System.Globalization;

namespace SeaPowerAIAdvisor;

internal static class SnapshotIds
{
    private static string Number(int index) => (index + 1).ToString(CultureInfo.InvariantCulture);
    internal static string Force(int index) => "own_force_" + Number(index);
    internal static string Ship(int force, int ship) => "own_ship_" + Number(force) + "_" + Number(ship);
    internal static string Base(int index) => "victory_base_" + Number(index);
    internal static string Contact(int index) => "contact_" + Number(index);
    internal static string Order(int index) => "published_order_" + Number(index);
    internal static string Report(int index) => "report_" + Number(index);
    internal static string AirUnit(int index) => "own_air_unit_" + Number(index);
    internal static string Flight(int index) => "own_flight_" + Number(index);
}
