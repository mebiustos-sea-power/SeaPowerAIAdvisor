using System;
using System.Globalization;
using System.Text;

namespace SeaPowerAIAdvisor;

internal static class SituationFormatter
{
    internal static string Format(SituationSnapshot snapshot, AdvisorText? language = null)
    {
        var text = language ?? AdvisorText.Load("en");
        var output = new StringBuilder();
        string T(string key) => text["diagnostic." + key];
        string F(string key, params object[] args) => text.Format("diagnostic." + key, args);
        output.AppendLine(T("header"));
        output.AppendLine(F("captured", Time(snapshot.At)));
        output.AppendLine(T("knowledge"));
        output.AppendLine(T("own_fleets"));
        if (snapshot.Forces.Count == 0) output.AppendLine(T("no_fleets"));
        foreach (var force in snapshot.Forces)
        {
            output.AppendLine(F("force", force.Name, Position(force.Position), force.Port == null ? T("at_sea") : F("in_port", force.Port)));
            output.AppendLine(F("speed_mission", Number(force.SpeedKnots), Label(text, "mission", force.Mission)));
            foreach (var ship in force.Ships)
                output.AppendLine(F("ship", ship.Name, ship.Type, ship.Nuclear ? T("nuclear") : Percent(ship.FuelShare),
                    ship.Unarmed ? T("unarmed") : Percent(ship.AmmoShare!.Value), Percent(ship.DamageShare)));
        }
        output.AppendLine("\n" + T("own_aviation"));
        if (snapshot.Aviation.Units.Count == 0) output.AppendLine(T("no_records"));
        foreach (var unit in snapshot.Aviation.Units)
        {
            var host = unit.BaseIndex.HasValue ? snapshot.Victory.Bases[unit.BaseIndex.Value].Name
                : unit.ForceIndex.HasValue ? snapshot.Forces[unit.ForceIndex.Value].Name : T("unknown");
            output.AppendLine(F("air_unit", unit.Name, host));
            output.AppendLine(F("launch_restriction", unit.LaunchBlocked.HasValue
                    ? unit.LaunchBlocked.Value ? T("yes") : T("no_displayed_block") : T("unknown")) +
                (unit.RunwayOutUntil.HasValue ? F("runway", Time(unit.RunwayOutUntil.Value)) : "") +
                (unit.DeckCycleRemaining.HasValue ? F("deck_cycle", unit.DeckCycleRemaining.Value) : "") +
                (unit.LaunchReason == null ? "" : " / " + unit.LaunchReason));
            foreach (var aircraft in unit.Aircraft)
            {
                output.AppendLine(F("aircraft", aircraft.Name, aircraft.CampaignLaunchSupported ? T("supported") : T("unsupported")));
                output.Append("    ");
                foreach (var count in aircraft.Counts)
                    output.Append(Label(text, "readiness", count.Key)).Append('=')
                        .Append(count.Value.HasValue ? count.Value.Value.ToString(CultureInfo.InvariantCulture) : T("unknown")).Append(" / ");
                output.AppendLine();
            }
        }
        output.AppendLine(T("aviation_caveat"));
        foreach (var flight in snapshot.Aviation.Flights)
            output.AppendLine(F("flight", flight.Name, flight.AircraftName, flight.Count, Position(flight.Position),
                flight.LoadoutName, flight.Returning ? T("returning") : ""));
        output.AppendLine("\n" + T("contacts"));
        if (snapshot.Contacts.Count == 0) output.AppendLine(T("no_contacts"));
        var index = 0;
        foreach (var contact in snapshot.Contacts)
        {
            output.AppendLine(F("contact", ++index, T("contact_kind." + contact.Kind), contact.Count,
                contact.Identified ? T("identified") : T("unidentified")));
            output.AppendLine(F("last_seen", Time(contact.SeenAt), Position(contact.Position)));
            output.AppendLine(F("age", Number((snapshot.At - contact.SeenAt).TotalHours), Number(contact.UncertaintyNm)));
        }
        output.AppendLine("\n" + T("bases"));
        if (snapshot.Bases.Count == 0) output.AppendLine(T("none"));
        foreach (var b in snapshot.Bases)
        {
            if (b.Kind != "AirBase" && b.Kind != "NavalBase" && b.Kind != "Port")
                throw new InvalidOperationException("Unknown base classification: " + b.Kind);
            output.AppendLine(F("base", b.Name, text["base_kind." + b.Kind], Position(b.Position)));
            output.AppendLine(b.LandSeenAt.HasValue
                ? F("ground_seen", Time(b.LandSeenAt.Value), b.Batteries!, b.SamSites!, b.GroundUnits!)
                : T("ground_unknown"));
            output.AppendLine(b.ForcesSeenAt.HasValue
                ? F("forces_seen", Time(b.ForcesSeenAt.Value), b.Ships!, b.Aircraft!)
                : T("forces_unknown"));
        }
        output.AppendLine("\n" + T("orders"));
        if (snapshot.Orders.Count == 0) output.AppendLine(T("none"));
        foreach (var order in snapshot.Orders)
        {
            output.AppendLine(F("order", order.Title, Label(text, "order_status", order.Status)));
            output.AppendLine(F("offered", Time(order.OfferedAt), order.Deadline.HasValue ? F("deadline", Time(order.Deadline.Value)) : ""));
            output.AppendLine(order.Briefing);
        }
        output.AppendLine("\n" + F("reports", CampaignSituationReader.ReportLimit));
        if (snapshot.Reports.Count == 0) output.AppendLine(T("none"));
        foreach (var report in snapshot.Reports) output.AppendLine(Time(report.At) + " " + report.Text);
        return output.ToString();
    }

    private static string Label(AdvisorText text, string group, string code) =>
        text.Contains(group + "." + code) ? text[group + "." + code] : code;
    private static string Position(MapPosition point) => Number(point.Latitude) + "°, " + Number(point.Longitude) + "°";
    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Percent(double value) => (value * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
    private static string Time(DateTime at) => at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z";
}
