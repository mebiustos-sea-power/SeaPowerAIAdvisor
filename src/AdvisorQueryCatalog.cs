using System;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Provider-neutral contract. A provider adapter can expose this as a function declaration later.
internal static class AdvisorQueryCatalog
{
    internal static readonly string[] Queries = {
        "catalog", "list_forces", "get_forces", "list_ships", "get_ships", "list_contacts",
        "list_bases", "get_base_observations", "list_orders", "get_orders", "list_reports", "get_reports",
        "list_air_units", "get_air_units", "list_air_flights"
    };

    internal static JArray Describe(SituationSnapshot snapshot) => new(
        Category("friendly_task_forces", snapshot.Forces.Count, "Own force names, positions and ship counts; selected force details.", "list_forces", "get_forces"),
        Category("friendly_ships", CountShips(snapshot), "Own ship names and types, then fuel, ammunition and damage.", "list_ships", "get_ships"),
        Category("friendly_air_units", snapshot.Aviation.Units.Count, "Own land squadrons and carrier air wings: host, aircraft types, counts, preparation states, campaign launch support and displayed constraints. Read details before recommending aircraft. No sortie feasibility or weapon/range guarantee.", "list_air_units", "get_air_units"),
        Category("friendly_air_flights", snapshot.Aviation.Flights.Count, "Own airborne flights: name, aircraft, position, count, selected loadout and return state. These aircraft overlap squadron/wing counts; do not add them again.", "list_air_flights", null),
        Category("observed_contacts", snapshot.Contacts.Count, "Observed enemy contacts with observation time and uncertainty; no live enemy state.", "list_contacts", null),
        Category("public_bases", snapshot.Victory.Bases.Count, "Public base identities and ownership; matching IDs are used by victory objectives.", "list_bases", null),
        Category("base_observations", snapshot.Bases.Count, "Recorded non-player base sightings only. Friendly-base stocks and defenses are not in this snapshot.", null, "get_base_observations"),
        Category("published_orders", snapshot.Orders.Count, "Published own orders and their briefing text.", "list_orders", "get_orders"),
        Category("recent_player_reports", snapshot.Reports.Count, "Metadata for the retained recent player reports; text is fetched by ID. At most 30 records are retained locally.", "list_reports", "get_reports")
    );

    internal static JObject Tool() => new()
    {
        ["name"] = "query_situation",
        ["description"] = "Read only this consultation's player-known snapshot. The host binds the active consultation; supply requests only. Lists are paginated. Use get_* directly for known IDs; group independent requests in one batch. Request missing evidence before advising, and state what remains unknown. Never infer enemy live state from a sighting.",
        ["parameters"] = new JObject
        {
            ["type"] = "object", ["additionalProperties"] = false,
            ["required"] = new JArray("requests"),
            ["properties"] = new JObject
            {
                ["requests"] = new JObject
                {
                    ["type"] = "array", ["minItems"] = 1, ["maxItems"] = AdvisorQuerySession.MaxQueriesPerRound,
                    ["items"] = new JObject
                    {
                        ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("query"),
                        ["properties"] = new JObject
                        {
                            ["query"] = new JObject { ["type"] = "string", ["enum"] = new JArray(Queries) },
                            ["ids"] = Ids("Required for get_* queries; snapshot-local IDs of the query's entity type."),
                            ["force_ids"] = Ids("Optional for list_ships and list_air_units; omit for all. For list_air_units, selects carrier wings hosted by these forces."),
                            ["base_ids"] = Ids("list_air_units only: select land squadrons hosted at these public base IDs. Do not combine with force_ids."),
                            ["offset"] = Integer("Zero-based list offset. List queries only.", 0, int.MaxValue),
                            ["limit"] = Integer("List page size. Omit for the default page size.", 1, AdvisorQuerySession.MaxPageSize),
                            ["include_ships"] = new JObject { ["type"] = "boolean", ["description"] = "get_forces only. Omit or false for force details without individual ships; true includes all ships in each selected force." },
                            ["kind"] = new JObject { ["type"] = "string", ["description"] = "list_reports only: a report kind code.",
                                ["enum"] = new JArray("contact", "battle", "landing", "fleet", "air", "supply", "production", "admiralty", "economy", "other") }
                        }
                    }
                }
            }
        }
    };

    private static JObject Category(string id, int count, string description, string? list, string? detail) => new()
    {
        ["category"] = id, ["item_count"] = count, ["description"] = description,
        ["list_query"] = list == null ? JValue.CreateNull() : new JValue(list),
        ["detail_query"] = detail == null ? JValue.CreateNull() : new JValue(detail)
    };
    private static JObject Ids(string description) => new()
    {
        ["type"] = "array", ["minItems"] = 1, ["maxItems"] = AdvisorQuerySession.MaxIdsPerQuery,
        ["uniqueItems"] = true, ["items"] = new JObject { ["type"] = "string" }, ["description"] = description
    };
    private static JObject Integer(string description, int min, int max) => new()
    { ["type"] = "integer", ["minimum"] = min, ["maximum"] = max, ["description"] = description };
    internal static int CountShips(SituationSnapshot snapshot)
    {
        var count = 0;
        foreach (var force in snapshot.Forces) count += force.Ships.Count;
        return count;
    }
}
