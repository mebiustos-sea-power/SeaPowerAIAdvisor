using System;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Explicit projection of an already filtered snapshot. This layer cannot access a simulation or resolve enemies.
internal static class AdvisorInputBuilder
{
    internal static string Build(SituationSnapshot snapshot, string replyLanguage, string? question)
    {
        var input = Header(replyLanguage, question);
        input["situation"] = new JObject
            {
                ["captured_at_utc"] = Time(snapshot.At),
                ["player_side_id"] = snapshot.Side,
                ["coverage"] = new JObject
                {
                    ["included"] = new JArray("victory_conditions", "friendly_task_forces", "friendly_air_units", "friendly_air_flights", "observed_contacts", "observed_non_player_bases", "published_orders", "recent_player_reports"),
                    ["not_included"] = new JArray("friendly_ground_forces", "friendly_base_stocks", "detailed_air_missions", "air_sortie_feasibility", "detailed_routes", "detailed_weapon_capabilities"),
                    ["report_limit"] = CampaignSituationReader.ReportLimit,
                    ["reference_scope"] = "current_snapshot"
                },
                ["victory_conditions"] = Victory(snapshot.Victory, snapshot.Side),
                ["friendly_task_forces"] = new JArray(snapshot.Forces.Select((f, i) => Force(f, i))),
                ["friendly_air_units"] = new JArray(snapshot.Aviation.Units.Select(AirUnit)),
                ["friendly_air_flights"] = new JArray(snapshot.Aviation.Flights.Select(AirFlight)),
                ["observed_contacts"] = new JArray(snapshot.Contacts.Select((c, i) => Contact(c, i, snapshot.At))),
                ["observed_non_player_bases"] = new JArray(snapshot.Bases.Select((b, i) => Base(b, i))),
                ["published_orders"] = new JArray(snapshot.Orders.Select((o, i) => Order(o, i))),
                ["recent_player_reports"] = new JArray(snapshot.Reports.Select(Report))
            };
        return input.ToString(Formatting.Indented);
    }

    internal static JObject Header(string replyLanguage, string? question, AdvisorPrompts? prompts = null, bool tactical = false)
    {
        replyLanguage = LanguageCode.Validate(replyLanguage);
        question ??= string.Empty;
        if (question.Length > AdvisorConversation.MaxQuestionLength)
            throw new ArgumentException("The question exceeds the supported length.", nameof(question));
        return new JObject
        {
            ["schema_version"] = 1,
            ["mode"] = "dynamic_campaign",
            ["reply_language"] = replyLanguage,
            ["instructions"] = (prompts ?? AdvisorPrompts.Current).Instructions(tactical),
            ["question"] = question
        };
    }


    internal static JObject AirUnit(FriendlyAirUnit unit, int index) => AirUnit(unit, index, true);
    internal static JObject AirUnit(FriendlyAirUnit unit, int index, bool details)
    {
        var result = new JObject
        {
            ["id"] = SnapshotIds.AirUnit(index), ["display_name"] = unit.Name, ["kind"] = unit.Kind,
            ["base_id"] = unit.BaseIndex.HasValue ? new JValue(SnapshotIds.Base(unit.BaseIndex.Value)) : JValue.CreateNull(),
            ["force_id"] = unit.ForceIndex.HasValue ? new JValue(SnapshotIds.Force(unit.ForceIndex.Value)) : JValue.CreateNull(),
            ["ship_id"] = unit.ForceIndex.HasValue && unit.ShipIndex.HasValue
                ? new JValue(SnapshotIds.Ship(unit.ForceIndex.Value, unit.ShipIndex.Value)) : JValue.CreateNull(),
            ["is_airlift_detachment"] = unit.AirliftDetachment,
            ["aircraft_type_count"] = unit.Aircraft.Count
        };
        if (!details) return result;
        result["launch_constraints"] = new JObject
        {
            ["displayed_launch_blocked"] = unit.LaunchBlocked.HasValue ? new JValue(unit.LaunchBlocked.Value) : JValue.CreateNull(),
            ["displayed_reason_text"] = Nullable(unit.LaunchReason),
            ["runway_out_until_utc"] = unit.RunwayOutUntil.HasValue ? new JValue(Time(unit.RunwayOutUntil.Value)) : JValue.CreateNull(),
            ["deck_cycle_remaining_aircraft"] = unit.DeckCycleRemaining.HasValue ? new JValue(unit.DeckCycleRemaining.Value) : JValue.CreateNull(),
            ["sortie_feasibility"] = "not_evaluated"
        };
        result["aircraft"] = new JArray(unit.Aircraft.Select(a => new JObject
        {
            ["display_name"] = a.Name, ["campaign_launch_supported"] = a.CampaignLaunchSupported,
            ["counts"] = new JObject(a.Counts.Select(c => new JProperty(c.Key, c.Value.HasValue ? new JValue(c.Value.Value) : JValue.CreateNull()))),
            ["selectable_loadout_names"] = new JArray(a.LoadoutNames)
        }));
        return result;
    }

    internal static JObject AirFlight(FriendlyAirFlight flight, int index) => new()
    {
        ["id"] = SnapshotIds.Flight(index), ["display_name"] = flight.Name,
        ["aircraft_display_name"] = flight.AircraftName, ["position"] = Position(flight.Position),
        ["aircraft_count"] = flight.Count, ["selected_loadout_name"] = flight.LoadoutName,
        ["returning"] = flight.Returning,
        ["source_air_unit_id"] = flight.SourceUnitIndex.HasValue ? new JValue(SnapshotIds.AirUnit(flight.SourceUnitIndex.Value)) : JValue.CreateNull()
    };

    internal static JObject Victory(VictorySnapshot victory, string playerSide) => new()
    {
        ["rules"] = new JObject
        {
            ["type"] = "first_side_to_hold_all_objective_bases",
            ["description"] = "The first side to hold all of its objective bases wins. Its own bases must also be held. Each side's objective list already applies opposing-homeland and side-specific exemptions.",
            ["requires_at_least_one_objective"] = true,
            ["victory_is_final"] = true,
            ["campaign_can_continue_after_victory"] = true
        },
        ["outcome"] = new JObject
        {
            ["status"] = victory.WinnerSide == null ? "undecided" : victory.WinnerSide == playerSide ? "player_victory" : "player_defeat",
            ["winner_side_id"] = Nullable(victory.WinnerSide),
            ["decided_at_utc"] = victory.DecidedAt.HasValue ? new JValue(Time(victory.DecidedAt.Value)) : JValue.CreateNull()
        },
        // Shared public base registry; side objective lists refer only to these snapshot-local IDs.
        ["bases"] = new JArray(victory.Bases.Select((b, i) => new JObject
        {
            ["id"] = VictoryBaseId(i),
            ["display_name"] = b.Name,
            ["kind"] = BaseKind(b.Kind),
            ["position"] = Position(b.Position),
            ["ownership_status"] = b.OwnerSide == null ? "unknown" : "known",
            ["current_owner_side_id"] = Nullable(b.OwnerSide)
        })),
        ["sides"] = new JArray(victory.Sides.Select(s => new JObject
        {
            ["side_id"] = s.Side,
            ["display_name"] = s.Name,
            ["relationship"] = s.Side == playerSide ? "player" : "opponent",
            ["progress"] = new JObject
            {
                ["held_count"] = s.Held,
                ["total_count"] = s.Objectives.Count,
                ["remaining_count"] = s.Objectives.Count - s.Held,
                ["all_objectives_held"] = s.Objectives.Count > 0 && s.Held == s.Objectives.Count
            },
            ["objective_bases"] = new JArray(s.Objectives.Select(i => new JObject
            {
                ["base_id"] = VictoryBaseId(i),
                ["status"] = victory.Bases[i].OwnerSide == s.Side ? "held" : "not_held"
            })),
            ["excluded_bases"] = new JArray(s.Excluded.Select(e => new JObject
            {
                ["base_id"] = VictoryBaseId(e.BaseIndex),
                ["reason"] = e.Reason switch
                {
                    "opposing_homeland" => "opposing_homeland",
                    "side_specific_exemption" => "side_specific_exemption",
                    _ => throw Unknown("victory exemption", e.Reason)
                }
            }))
        }))
    };

    private static string VictoryBaseId(int index) => SnapshotIds.Base(index);
    internal static string BaseKind(string kind) => kind switch
    {
        "NavalBase" => "naval_base", "Port" => "port", "AirBase" => "air_base",
        _ => throw Unknown("base kind", kind)
    };

    internal static JObject Force(FriendlyForce force, int index) => new()
    {
        ["id"] = SnapshotIds.Force(index),
        ["display_name"] = force.Name,
        ["position"] = Position(force.Position),
        ["speed_setting_knots"] = force.SpeedKnots,
        ["location"] = new JObject
        {
            ["status"] = force.Port == null ? "at_sea" : "in_port",
            ["port_display_name"] = Nullable(force.Port)
        },
        ["is_convoy"] = force.IsConvoy,
        ["assigned_group_mission"] = new JObject
        {
            ["status"] = force.Mission == "None" ? "not_assigned" : "assigned",
            ["kind"] = force.Mission == "None" ? JValue.CreateNull() : new JValue(Mission(force.Mission))
        },
        ["ships"] = new JArray(force.Ships.Select((s, i) => Ship(s, index, i)))
    };

    internal static JObject Ship(FriendlyShip ship, int forceIndex, int index) => new()
    {
        ["id"] = SnapshotIds.Ship(forceIndex, index),
        ["display_name"] = ship.Name,
        ["type_code"] = ship.Type,
        ["is_merchant"] = ship.Merchant,
        ["fuel"] = new JObject
        {
            ["propulsion"] = ship.Nuclear ? "nuclear" : "non_nuclear",
            ["status"] = ship.Nuclear ? "not_applicable" : "known",
            ["remaining_fraction"] = ship.Nuclear ? JValue.CreateNull() : new JValue(ship.FuelShare)
        },
        ["ammunition"] = new JObject
        {
            ["status"] = ship.Unarmed ? "unarmed" : "known",
            ["remaining_fraction"] = ship.Unarmed ? JValue.CreateNull() : new JValue(ship.AmmoShare
                ?? throw new InvalidOperationException("An armed own ship is missing its ammunition level."))
        },
        ["damage_fraction"] = ship.DamageShare
    };

    internal static JObject Contact(ObservedContact c, int index, DateTime at) => new()
    {
        ["id"] = SnapshotIds.Contact(index),
        ["kind"] = c.Kind switch
        {
            "Surface" => "surface", "Submarine" => "submarine", "Air" => "air",
            _ => throw Unknown("contact kind", c.Kind)
        },
        ["identification_status"] = c.Identified ? "identified" : "unidentified",
        ["observed_count"] = c.Count,
        ["last_observed_at_utc"] = Time(c.SeenAt),
        ["observed_position"] = Position(c.Position),
        ["observation_age_hours"] = (at - c.SeenAt).TotalHours,
        ["estimated_position_uncertainty_radius_nm"] = c.UncertaintyNm
    };

    internal static JObject Base(ObservedBase b, int index) => new()
    {
        ["id"] = "base_" + (index + 1).ToString(CultureInfo.InvariantCulture),
        ["public_base_id"] = SnapshotIds.Base(b.PublicBaseIndex),
        ["display_name"] = b.Name,
        ["kind"] = BaseKind(b.Kind),
        ["position"] = Position(b.Position),
        ["ground_defenses"] = new JObject
        {
            ["status"] = b.LandSeenAt.HasValue ? "observed" : "unknown",
            ["last_observed_at_utc"] = b.LandSeenAt.HasValue ? new JValue(Time(b.LandSeenAt.Value)) : JValue.CreateNull(),
            ["batteries"] = b.LandSeenAt.HasValue ? Count(b.Batteries) : JValue.CreateNull(),
            ["sam_sites"] = b.LandSeenAt.HasValue ? Count(b.SamSites) : JValue.CreateNull(),
            ["ground_units"] = b.LandSeenAt.HasValue ? Count(b.GroundUnits) : JValue.CreateNull()
        },
        ["ships_and_aircraft"] = new JObject
        {
            ["status"] = b.ForcesSeenAt.HasValue ? "observed" : "unknown",
            ["last_observed_at_utc"] = b.ForcesSeenAt.HasValue ? new JValue(Time(b.ForcesSeenAt.Value)) : JValue.CreateNull(),
            ["ships_in_port"] = b.ForcesSeenAt.HasValue ? Count(b.Ships) : JValue.CreateNull(),
            ["aircraft"] = b.ForcesSeenAt.HasValue ? Count(b.Aircraft) : JValue.CreateNull()
        }
    };

    internal static JObject Order(PublishedOrder order, int index) => new()
    {
        ["id"] = SnapshotIds.Order(index),
        ["display_name"] = order.Title,
        ["briefing_text"] = order.Briefing,
        ["status"] = order.Status switch
        {
            "Offered" => "offered", "Active" => "active", "Completed" => "completed", "Failed" => "failed",
            "Declined" => "declined", "Lapsed" => "lapsed", "Cancelled" => "cancelled",
            _ => throw Unknown("published order status", order.Status)
        },
        ["offered_at_utc"] = Time(order.OfferedAt),
        ["deadline_utc"] = order.Deadline.HasValue ? new JValue(Time(order.Deadline.Value)) : JValue.CreateNull()
    };

    internal static JObject Report(PublishedReport report) => new()
    {
        ["published_at_utc"] = Time(report.At),
        ["kind"] = report.Kind switch
        {
            "Contact" => "contact", "Battle" => "battle", "Landing" => "landing", "Fleet" => "fleet", "Air" => "air",
            "Supply" => "supply", "Production" => "production", "Admiralty" => "admiralty", "Economy" => "economy", "Other" => "other",
            _ => throw Unknown("report kind", report.Kind)
        },
        ["text"] = report.Text
    };

    private static string Mission(string kind) => kind switch
    {
        "Patrol" => "patrol", "Hunt" => "hunt", "Escort" => "escort", "Cap" => "combat_air_patrol", "Cover" => "cover",
        "Search" => "search", "Follow" => "follow", "Rendezvous" => "rendezvous", "Join" => "join", "Invade" => "invade",
        "StrikeBase" => "strike_base", "Replenish" => "replenish", "Refuel" => "refuel", "TankerStation" => "tanker_station",
        _ => throw Unknown("assigned group mission", kind)
    };

    internal static JObject Position(MapPosition p)
    {
        if (double.IsNaN(p.Latitude) || double.IsInfinity(p.Latitude) || p.Latitude < -90 || p.Latitude > 90 ||
            double.IsNaN(p.Longitude) || double.IsInfinity(p.Longitude))
            throw new InvalidOperationException("The snapshot contains an invalid geographical position.");
        var longitude = p.Longitude % 360;
        if (longitude >= 180) longitude -= 360;
        if (longitude < -180) longitude += 360;
        return new JObject { ["latitude_deg"] = p.Latitude, ["longitude_deg"] = longitude };
    }

    internal static string Time(DateTime value)
    {
        if (value.Kind == DateTimeKind.Local)
            throw new InvalidOperationException("Campaign time must be UTC or unspecified UTC, not host-local time.");
        return DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);
    }
    private static JValue Nullable(string? value) => value == null ? JValue.CreateNull() : new JValue(value);
    private static JValue Count(int? value) => value.HasValue && value.Value >= 0 ? new JValue(value.Value)
        : throw new InvalidOperationException("An observed base count is missing or invalid.");
    private static InvalidOperationException Unknown(string field, string value) => new("Unknown " + field + ": " + value);
}
