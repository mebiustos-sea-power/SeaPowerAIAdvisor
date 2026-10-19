using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal enum InformationKind
{
    BattleUnits, BattleFuel, BattleDamage, BattleWeapons, BattleContacts, BattleThreats, BattleBriefing, BattleReports,
    CampaignForces, CampaignShips, CampaignFuel, CampaignAmmunition, CampaignDamage, CampaignAviation,
    CampaignContacts, CampaignBases, CampaignOrders, CampaignReports,
    BattleGroundUnits, BattleBases, BattleAviation, CampaignOwnBases, CampaignGroundUnits, BattleNeutralContacts, BattleDestroyedContacts,
    BattleLaunchedWeapons, BattleWeaponContacts, BattleSensors, BattleSensorPerformance
}

internal sealed class InformationItem
{
    internal InformationKind Kind { get; }
    internal string Id { get; }
    internal string TextKey { get; }
    internal InformationKind? Requires { get; }
    internal bool DefaultEnabled { get; }
    internal InformationItem(InformationKind kind, string id, string label, InformationKind? requires = null, bool defaultEnabled = true)
    { Kind = kind; Id = id; TextKey = "information." + label; Requires = requires; DefaultEnabled = defaultEnabled; }
}

internal sealed class InformationGroup
{
    internal string TextKey { get; }
    internal bool Battle { get; }
    internal IReadOnlyList<InformationKind> Items { get; }
    internal InformationGroup(bool battle, string label, params InformationKind[] items)
    { Battle = battle; TextKey = "information." + label; Items = Array.AsReadOnly(items); }
}

// Selection is immutable and independent of the provider and live game. Dependencies are effective permissions.
internal sealed class AdvisorInformation
{
    internal static readonly IReadOnlyList<InformationItem> Items = Array.AsReadOnly(new[]
    {
        new InformationItem(InformationKind.BattleUnits, "battle_units", "basic"),
        new InformationItem(InformationKind.BattleFuel, "battle_fuel", "fuel", InformationKind.BattleUnits),
        new InformationItem(InformationKind.BattleDamage, "battle_damage", "damage", InformationKind.BattleUnits),
        new InformationItem(InformationKind.BattleWeapons, "battle_weapons", "weapons", InformationKind.BattleUnits),
        new InformationItem(InformationKind.BattleSensors, "battle_sensors", "sensor_status", InformationKind.BattleUnits),
        new InformationItem(InformationKind.BattleSensorPerformance, "battle_sensor_performance", "sensor_performance", InformationKind.BattleUnits, defaultEnabled: false),
        new InformationItem(InformationKind.BattleLaunchedWeapons, "battle_launched_weapons", "launched_weapons", InformationKind.BattleUnits),
        new InformationItem(InformationKind.BattleContacts, "battle_contacts", "contacts"),
        new InformationItem(InformationKind.BattleWeaponContacts, "battle_weapon_contacts", "weapon_contacts", InformationKind.BattleContacts),
        new InformationItem(InformationKind.BattleNeutralContacts, "battle_neutral_contacts", "neutral", InformationKind.BattleContacts, defaultEnabled: false),
        new InformationItem(InformationKind.BattleDestroyedContacts, "battle_destroyed_contacts", "destroyed", InformationKind.BattleContacts, defaultEnabled: false),
        new InformationItem(InformationKind.BattleThreats, "battle_threats", "threats", InformationKind.BattleContacts),
        new InformationItem(InformationKind.BattleBriefing, "battle_briefing", "briefing"),
        new InformationItem(InformationKind.BattleReports, "battle_reports", "reports"),
        new InformationItem(InformationKind.CampaignForces, "campaign_forces", "forces"),
        new InformationItem(InformationKind.CampaignShips, "campaign_ships", "ships", InformationKind.CampaignForces),
        new InformationItem(InformationKind.CampaignFuel, "campaign_fuel", "fuel", InformationKind.CampaignShips),
        new InformationItem(InformationKind.CampaignAmmunition, "campaign_ammunition", "ammunition", InformationKind.CampaignShips),
        new InformationItem(InformationKind.CampaignDamage, "campaign_damage", "damage", InformationKind.CampaignShips),
        new InformationItem(InformationKind.CampaignAviation, "campaign_aviation", "aviation"),
        new InformationItem(InformationKind.CampaignContacts, "campaign_contacts", "contacts"),
        new InformationItem(InformationKind.CampaignBases, "campaign_base_observations", "base_observations"),
        new InformationItem(InformationKind.CampaignOrders, "campaign_orders", "orders"),
        new InformationItem(InformationKind.CampaignReports, "campaign_reports", "reports"),
        new InformationItem(InformationKind.BattleGroundUnits, "battle_ground_units", "own_ground", InformationKind.BattleUnits),
        new InformationItem(InformationKind.BattleBases, "battle_bases", "own_bases", InformationKind.BattleUnits),
        new InformationItem(InformationKind.BattleAviation, "battle_aviation", "hosted_aviation", InformationKind.BattleUnits),
        new InformationItem(InformationKind.CampaignOwnBases, "campaign_own_bases", "own_base_status"),
        new InformationItem(InformationKind.CampaignGroundUnits, "campaign_ground_units", "own_ground")
    });
    internal static readonly IReadOnlyList<InformationGroup> Groups = Array.AsReadOnly(new[]
    {
        new InformationGroup(true, "battle_friendly", InformationKind.BattleUnits, InformationKind.BattleGroundUnits, InformationKind.BattleBases, InformationKind.BattleAviation, InformationKind.BattleFuel, InformationKind.BattleDamage, InformationKind.BattleWeapons, InformationKind.BattleSensors, InformationKind.BattleSensorPerformance, InformationKind.BattleLaunchedWeapons),
        new InformationGroup(true, "battle_intelligence", InformationKind.BattleContacts, InformationKind.BattleWeaponContacts, InformationKind.BattleNeutralContacts, InformationKind.BattleDestroyedContacts, InformationKind.BattleThreats, InformationKind.BattleBriefing, InformationKind.BattleReports),
        new InformationGroup(false, "campaign_friendly", InformationKind.CampaignForces, InformationKind.CampaignShips, InformationKind.CampaignFuel, InformationKind.CampaignAmmunition, InformationKind.CampaignDamage, InformationKind.CampaignAviation, InformationKind.CampaignOwnBases, InformationKind.CampaignGroundUnits),
        new InformationGroup(false, "campaign_intelligence", InformationKind.CampaignContacts, InformationKind.CampaignBases, InformationKind.CampaignOrders, InformationKind.CampaignReports)
    });
    internal static readonly AdvisorInformation All = new(Items.ToDictionary(i => i.Id, _ => true, StringComparer.Ordinal));
    internal static readonly AdvisorInformation Default = new(Items.ToDictionary(i => i.Id, i => i.DefaultEnabled, StringComparer.Ordinal));
    private readonly Dictionary<string, bool> selected;
    private AdvisorInformation(Dictionary<string, bool> selected) { this.selected = selected; }
    internal static InformationItem Item(InformationKind kind) => Items.Single(i => i.Kind == kind);
    internal bool Allows(InformationKind kind)
    {
        var item = Item(kind);
        return selected[item.Id] && (!item.Requires.HasValue || Allows(item.Requires.Value));
    }
    internal bool CanEdit(InformationKind kind) => !Item(kind).Requires.HasValue || Allows(Item(kind).Requires!.Value);
    internal AdvisorInformation With(InformationKind kind, bool enabled)
    {
        var next = new Dictionary<string, bool>(selected, StringComparer.Ordinal);
        InformationKind? current = kind;
        do
        {
            var item = Item(current.Value); next[item.Id] = enabled;
            current = enabled ? item.Requires : null;
        } while (current.HasValue);
        return new AdvisorInformation(next);
    }
    internal AdvisorInformation With(InformationGroup group, bool enabled)
    {
        var next = this;
        foreach (var kind in group.Items) next = next.With(kind, enabled);
        return next;
    }
    internal bool? State(InformationGroup group) => group.Items.All(Allows) ? true : group.Items.Any(Allows) ? (bool?)null : false;
    internal string Key => string.Join(",", Items.Where(i => !Allows(i.Kind)).Select(i => i.Id));
    // Save explicit choices, including children hidden by a disabled parent. Defaults only fill newly introduced items.
    internal JObject Data() => new(Items.Select(i => new JProperty(i.Id, selected[i.Id])));
    internal static AdvisorInformation ParseSelections(JToken? token)
    {
        if (token == null) return Default;
        if (!(token is JObject values) || values.Properties().Any(p => p.Value.Type != JTokenType.Boolean || !Items.Any(i => i.Id == p.Name)))
            throw new AdvisorOptionsException("information_invalid");
        return new AdvisorInformation(Items.ToDictionary(i => i.Id, i => (bool?)values[i.Id] ?? i.DefaultEnabled, StringComparer.Ordinal));
    }
    // Legacy files stored only disabled IDs. Missing legacy items were explicitly on under that format.
    internal static AdvisorInformation Parse(JToken? token)
    {
        if (token == null) return Default;
        if (!(token is JArray array) || array.Any(i => i.Type != JTokenType.String) ||
            array.Values<string>().Distinct(StringComparer.Ordinal).Count() != array.Count ||
            array.Values<string>().Any(id => !Items.Any(i => i.Id == id)))
            throw new AdvisorOptionsException("information_invalid");
        var disabled = array.Values<string>().ToHashSet(StringComparer.Ordinal);
        return new AdvisorInformation(Items.ToDictionary(i => i.Id,
            i => !disabled.Contains(i.Id) && ((int)i.Kind <= (int)InformationKind.CampaignReports || i.DefaultEnabled), StringComparer.Ordinal));
    }
    internal JArray Excluded(bool battle) => new(Items.Where(i => i.Id.StartsWith(battle ? "battle_" : "campaign_", StringComparison.Ordinal) && !Allows(i.Kind)).Select(i => i.Id));
    internal void AddCoverage(JObject initial, bool battle)
    {
        initial["situation"]!["coverage"]!["excluded_by_user"] = Excluded(battle);
    }
    internal bool CampaignCategory(string category) => category switch
    {
        "friendly_task_forces" => Allows(InformationKind.CampaignForces),
        "friendly_ships" => Allows(InformationKind.CampaignShips),
        "friendly_air_units" or "friendly_air_flights" => Allows(InformationKind.CampaignAviation),
        "observed_contacts" => Allows(InformationKind.CampaignContacts),
        "friendly_bases" => Allows(InformationKind.CampaignOwnBases),
        "friendly_ground_units" => Allows(InformationKind.CampaignGroundUnits),
        "base_observations" => Allows(InformationKind.CampaignBases),
        "published_orders" => Allows(InformationKind.CampaignOrders),
        "recent_player_reports" => Allows(InformationKind.CampaignReports),
        "public_bases" => true,
        _ => throw new InvalidOperationException("Unmapped campaign category: " + category)
    };
    internal bool CampaignQuery(string query) => query switch
    {
        "catalog" or "list_bases" => true,
        "list_forces" or "get_forces" => Allows(InformationKind.CampaignForces),
        "list_ships" or "get_ships" => Allows(InformationKind.CampaignShips),
        "list_air_units" or "get_air_units" or "list_air_flights" => Allows(InformationKind.CampaignAviation),
        "list_contacts" => Allows(InformationKind.CampaignContacts),
        "get_bases" => Allows(InformationKind.CampaignOwnBases),
        "list_ground_units" or "get_ground_units" => Allows(InformationKind.CampaignGroundUnits),
        "get_base_observations" => Allows(InformationKind.CampaignBases),
        "list_orders" or "get_orders" => Allows(InformationKind.CampaignOrders),
        "list_reports" or "get_reports" => Allows(InformationKind.CampaignReports),
        _ => throw new AdvisorQueryException("unknown_query", "Use a query from the current catalogue.")
    };
    internal static readonly string[] BattleCapabilitySections = { "fuel", "damage", "weapons", "sensors", "sensor_performance" };
    internal string[] BattleSections => BattleCapabilitySections.Where(s => Allows(s switch
    {
        "fuel" => InformationKind.BattleFuel, "damage" => InformationKind.BattleDamage,
        "weapons" => InformationKind.BattleWeapons, "sensors" => InformationKind.BattleSensors,
        "sensor_performance" => InformationKind.BattleSensorPerformance,
        _ => throw new InvalidOperationException("Unmapped battle capability section: " + s)
    })).ToArray();
    internal bool BattleCategory(string category) => category switch
    {
        "friendly_units" => Allows(InformationKind.BattleUnits),
        "friendly_ground_units" => Allows(InformationKind.BattleGroundUnits),
        "friendly_bases" => Allows(InformationKind.BattleBases),
        "hosted_air_units" or "aviation_tasks" => Allows(InformationKind.BattleAviation),
        "unit_capabilities" => BattleSections.Length > 0,
        "friendly_launched_weapons" => Allows(InformationKind.BattleLaunchedWeapons),
        "observed_contacts" => Allows(InformationKind.BattleContacts),
        "public_class_threat_references" => Allows(InformationKind.BattleThreats),
        "mission_briefing" => Allows(InformationKind.BattleBriefing),
        "published_reports" => Allows(InformationKind.BattleReports),
        _ => throw new InvalidOperationException("Unmapped battle category: " + category)
    };
    internal bool BattleQuery(string query) => query switch
    {
        "catalog" => true,
        "list_units" or "get_units" => Allows(InformationKind.BattleUnits),
        "list_launched_weapons" => Allows(InformationKind.BattleLaunchedWeapons),
        "list_ground_units" or "get_ground_units" => Allows(InformationKind.BattleGroundUnits),
        "list_bases" or "get_bases" => Allows(InformationKind.BattleBases),
        "list_air_units" or "get_air_units" or "list_air_tasks" => Allows(InformationKind.BattleAviation),
        "get_unit_capabilities" => BattleSections.Length > 0,
        "list_contacts" or BattleContactAssessment.Query => Allows(InformationKind.BattleContacts),
        "get_contact_threats" => Allows(InformationKind.BattleThreats),
        "get_briefing" => Allows(InformationKind.BattleBriefing),
        "list_reports" or "get_reports" => Allows(InformationKind.BattleReports),
        _ => throw new AdvisorQueryException("unknown_query", "Use a query from the current catalogue.")
    };
}
