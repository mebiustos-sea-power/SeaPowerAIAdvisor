using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Provider-neutral contract. A provider adapter can expose this as a function declaration later.
internal static class AdvisorQueryCatalog
{
    internal static readonly string[] Queries = {
        "catalog", "list_forces", "get_forces", "list_ships", "get_ships", "list_contacts",
        "list_bases", "get_bases", "list_ground_units", "get_ground_units", "get_base_observations", "list_orders", "get_orders", "list_reports", "get_reports",
        "list_air_units", "get_air_units", "list_air_flights"
    };

    internal static JArray Describe(SituationSnapshot snapshot, AdvisorInformation? selection = null)
    {
        var information = selection ?? AdvisorInformation.Default;
        var rows = new JArray(
        Category("friendly_task_forces", snapshot.Forces.Count, "Own force identities, positions and ship counts.", "list_forces", "get_forces"),
        Category("friendly_ships", CountShips(snapshot), "Own ship identities, types and enabled status sections.", "list_ships", "get_ships"),
        Category("friendly_air_units", snapshot.Aviation.Units.Count, "Own land squadrons/carrier wings: host, types, counts, readiness and displayed launch constraints.", "list_air_units", "get_air_units"),
        Category("friendly_air_flights", snapshot.Aviation.Flights.Count, "Own airborne flight identities, positions, counts, selected loadout and return state.", "list_air_flights", null),
        Category("observed_contacts", snapshot.Contacts.Count, "Observed contacts, observation time and uncertainty.", "list_contacts", null),
        Category("public_bases", snapshot.Victory.Bases.Count, "Public base identities/ownership; IDs match victory objectives.", "list_bases", null),
        Category("friendly_bases", snapshot.Ground.Bases().Count, "Own base capacity, shipyard, recorded stocks and enabled garrison strength. IDs from list_bases.", null, "get_bases"),
        Category("friendly_ground_units", snapshot.Ground.Units().Count, "Own ground identities, campaign strength and disclosed base/transport.", "list_ground_units", "get_ground_units"),
        Category("base_observations", snapshot.Bases.Count, "Recorded non-player base sightings.", null, "get_base_observations"),
        Category("published_orders", snapshot.Orders.Count, "Published own orders and briefing text.", "list_orders", "get_orders"),
        Category("recent_player_reports", snapshot.Reports.Count, "Retained player-report metadata; body by ID.", "list_reports", "get_reports")
        );
        return new JArray(rows.OfType<JObject>().Where(r => information.CampaignCategory((string)r["category"]!)));
    }

    internal static JObject Tool(AdvisorInformation? selection = null)
    {
        var information = selection ?? AdvisorInformation.Default;
        var tool = RawTool();
        var properties = (JObject)tool["parameters"]!["properties"]!["requests"]!["items"]!["properties"]!;
        properties["query"]!["enum"] = new JArray(Queries.Where(information.CampaignQuery));
        if (!information.Allows(InformationKind.CampaignShips)) properties.Remove("include_ships");
        if (!information.Allows(InformationKind.CampaignForces)) properties.Remove("force_ids");
        return tool;
    }

    private static JObject RawTool() => new()
    {
        ["name"] = "query_situation",
        ["description"] = "Query the current player-known campaign snapshot. Supply a requests batch; the host binds the consultation.",
        ["parameters"] = new JObject
        {
            ["type"] = "object", ["additionalProperties"] = false,
            ["required"] = new JArray("requests"),
            ["properties"] = new JObject
            {
                ["requests"] = new JObject
                {
                    ["type"] = "array", ["minItems"] = 1, ["maxItems"] = AdvisorQueryLimits.MaxQueriesPerRound,
                    ["items"] = new JObject
                    {
                        ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("query"),
                        ["properties"] = new JObject
                        {
                            ["query"] = new JObject { ["type"] = "string", ["enum"] = new JArray(Queries) },
                            ["ids"] = Ids("get_* only: current IDs of the requested entity type."),
                            ["force_ids"] = Ids("list_ships/list_air_units: optional force filter; air units selects hosted wings."),
                            ["base_ids"] = Ids("list_air_units: land squadrons at these public bases; incompatible with force_ids."),
                            ["offset"] = Integer("Zero-based list offset. List queries only.", 0, int.MaxValue),
                            ["limit"] = Integer("List page size. Omit for the default page size.", 1, AdvisorQueryLimits.MaxPageSize),
                            ["include_ships"] = new JObject { ["type"] = "boolean", ["description"] = "get_forces: true includes individual ships; omitted/false returns force details only." },
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
        ["type"] = "array", ["minItems"] = 1, ["maxItems"] = AdvisorQueryLimits.MaxIdsPerQuery,
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
