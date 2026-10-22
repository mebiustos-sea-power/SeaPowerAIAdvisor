using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Provider-neutral contract. A provider adapter can expose this as a function declaration later.
internal static class AdvisorQueryCatalog
{
    internal static readonly AdvisorRequestShape[] RequestShapes = {
        new(new[] { "catalog" }, Array.Empty<string>()),
        new(new[] { "list_forces", "list_contacts", "list_bases", "list_ground_units", "list_orders", "list_air_flights" }, Array.Empty<string>(), "offset", "limit"),
        new(new[] { "get_ships", "get_bases", "get_ground_units", "get_base_observations", "get_orders", "get_reports", "get_air_units" }, new[] { "ids" }),
        new(new[] { "get_forces" }, new[] { "ids" }, "include_ships"),
        new(new[] { "list_ships" }, Array.Empty<string>(), "offset", "limit", "force_ids"),
        new(new[] { "list_air_units" }, Array.Empty<string>(), "offset", "limit", "force_ids"),
        new(new[] { "list_air_units" }, Array.Empty<string>(), "offset", "limit", "base_ids"),
        new(new[] { "list_reports" }, Array.Empty<string>(), "offset", "limit", "kind")
    };
    internal static string[] Queries => RequestShapes.SelectMany(s => s.Names).Distinct(StringComparer.Ordinal).ToArray();

    internal static JArray Describe(SituationSnapshot snapshot, AdvisorInformation? selection = null, AdvisorPrompts? prompts = null)
    {
        var information = selection ?? AdvisorInformation.Default;
        prompts ??= AdvisorPrompts.Current;
        var rows = new JArray(
        Category("friendly_task_forces", snapshot.Forces.Count, prompts.Text("campaign.json", "catalog_friendly_task_forces"), "list_forces", "get_forces"),
        Category("friendly_ships", CountShips(snapshot), prompts.Text("campaign.json", "catalog_friendly_ships"), "list_ships", "get_ships"),
        Category("friendly_air_units", snapshot.Aviation.Units.Count, prompts.Text("campaign.json", "catalog_friendly_air_units"), "list_air_units", "get_air_units"),
        Category("friendly_air_flights", snapshot.Aviation.Flights.Count, prompts.Text("campaign.json", "catalog_friendly_air_flights"), "list_air_flights", null),
        Category("observed_contacts", snapshot.Contacts.Count, prompts.Text("campaign.json", "catalog_observed_contacts"), "list_contacts", null),
        Category("public_bases", snapshot.Victory.Bases.Count, prompts.Text("campaign.json", "catalog_public_bases"), "list_bases", null),
        Category("friendly_bases", snapshot.Ground.Bases().Count, prompts.Text("campaign.json", "catalog_friendly_bases"), null, "get_bases"),
        Category("friendly_ground_units", snapshot.Ground.Units().Count, prompts.Text("campaign.json", "catalog_friendly_ground_units"), "list_ground_units", "get_ground_units"),
        Category("base_observations", snapshot.Bases.Count, prompts.Text("campaign.json", "catalog_base_observations"), null, "get_base_observations"),
        Category("published_orders", snapshot.Orders.Count, prompts.Text("campaign.json", "catalog_published_orders"), "list_orders", "get_orders"),
        Category("recent_player_reports", snapshot.Reports.Count, prompts.Text("campaign.json", "catalog_recent_player_reports"), "list_reports", "get_reports")
        );
        return new JArray(rows.OfType<JObject>().Where(r => information.CampaignCategory((string)r["category"]!)));
    }

    internal static JObject Tool(AdvisorInformation? selection = null, AdvisorPrompts? prompts = null)
    {
        var information = selection ?? AdvisorInformation.Default;
        prompts ??= AdvisorPrompts.Current;
        var tool = RawTool(prompts);
        var properties = (JObject)tool["parameters"]!["properties"]!["requests"]!["items"]!["properties"]!;
        properties["query"]!["enum"] = new JArray(Queries.Where(information.CampaignQuery));
        if (!information.Allows(InformationKind.CampaignShips)) properties.Remove("include_ships");
        if (!information.Allows(InformationKind.CampaignForces)) properties.Remove("force_ids");
        tool["parameters"]!["properties"]!["requests"]!["items"] =
            AdvisorRequestShape.Union("query", RequestShapes, properties, Queries.Where(information.CampaignQuery));
        return tool;
    }

    private static JObject RawTool(AdvisorPrompts prompts) => new()
    {
        ["name"] = "query_situation",
        ["description"] = prompts.Text("campaign.json", "tool_description"),
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
                            ["ids"] = Ids(prompts.Text("campaign.json", "tool_ids")),
                            ["force_ids"] = Ids(prompts.Text("campaign.json", "tool_force_ids")),
                            ["base_ids"] = Ids(prompts.Text("campaign.json", "tool_base_ids")),
                            ["offset"] = Integer(prompts.Text("campaign.json", "tool_offset"), 0, int.MaxValue),
                            ["limit"] = Integer(prompts.Text("campaign.json", "tool_limit"), 1, AdvisorQueryLimits.MaxPageSize),
                            ["include_ships"] = new JObject { ["type"] = "boolean", ["description"] = prompts.Text("campaign.json", "tool_include_ships") },
                            ["kind"] = new JObject { ["type"] = "string", ["description"] = prompts.Text("campaign.json", "tool_kind"),
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
        ["uniqueItems"] = true, ["items"] = new JObject { ["type"] = "string", ["minLength"] = 1 }, ["description"] = description
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
