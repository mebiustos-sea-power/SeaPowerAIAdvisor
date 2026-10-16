using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal enum InformationKind
{
    BattleUnits, BattleFuel, BattleDamage, BattleWeapons, BattleContacts, BattleThreats, BattleBriefing, BattleReports,
    CampaignForces, CampaignShips, CampaignFuel, CampaignAmmunition, CampaignDamage, CampaignAviation,
    CampaignContacts, CampaignBases, CampaignOrders, CampaignReports
}

internal sealed class InformationItem
{
    internal InformationKind Kind { get; }
    internal string Id { get; }
    internal string TextKey { get; }
    internal InformationKind? Requires { get; }
    internal InformationItem(InformationKind kind, string id, string label, InformationKind? requires = null)
    { Kind = kind; Id = id; TextKey = "information." + label; Requires = requires; }
}

internal sealed class InformationGroup
{
    internal string TextKey { get; }
    internal IReadOnlyList<InformationKind> Items { get; }
    internal InformationGroup(string label, params InformationKind[] items)
    { TextKey = "information." + label; Items = Array.AsReadOnly(items); }
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
        new InformationItem(InformationKind.BattleContacts, "battle_contacts", "contacts"),
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
        new InformationItem(InformationKind.CampaignReports, "campaign_reports", "reports")
    });
    internal static readonly IReadOnlyList<InformationGroup> Groups = Array.AsReadOnly(new[]
    {
        new InformationGroup("battle_friendly", InformationKind.BattleUnits, InformationKind.BattleFuel, InformationKind.BattleDamage, InformationKind.BattleWeapons),
        new InformationGroup("battle_intelligence", InformationKind.BattleContacts, InformationKind.BattleThreats, InformationKind.BattleBriefing, InformationKind.BattleReports),
        new InformationGroup("campaign_friendly", InformationKind.CampaignForces, InformationKind.CampaignShips, InformationKind.CampaignFuel, InformationKind.CampaignAmmunition, InformationKind.CampaignDamage, InformationKind.CampaignAviation),
        new InformationGroup("campaign_intelligence", InformationKind.CampaignContacts, InformationKind.CampaignBases, InformationKind.CampaignOrders, InformationKind.CampaignReports)
    });
    internal static readonly AdvisorInformation All = new(Array.Empty<string>());
    private readonly HashSet<string> disabled;
    private AdvisorInformation(IEnumerable<string> disabled) { this.disabled = new HashSet<string>(disabled, StringComparer.Ordinal); }
    internal static InformationItem Item(InformationKind kind) => Items.Single(i => i.Kind == kind);
    internal bool Allows(InformationKind kind)
    {
        var item = Item(kind);
        return !disabled.Contains(item.Id) && (!item.Requires.HasValue || Allows(item.Requires.Value));
    }
    internal bool CanEdit(InformationKind kind) => !Item(kind).Requires.HasValue || Allows(Item(kind).Requires!.Value);
    internal AdvisorInformation With(InformationKind kind, bool enabled)
    {
        var next = new HashSet<string>(disabled, StringComparer.Ordinal);
        if (enabled)
        {
            InformationKind? current = kind;
            while (current.HasValue)
            {
                var item = Item(current.Value); next.Remove(item.Id); current = item.Requires;
            }
        }
        else next.Add(Item(kind).Id);
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
    internal JArray Data() => new(Items.Where(i => disabled.Contains(i.Id)).Select(i => i.Id));
    internal static AdvisorInformation Parse(JToken? token)
    {
        if (token == null) return All;
        if (!(token is JArray array) || array.Any(i => i.Type != JTokenType.String) ||
            array.Values<string>().Distinct(StringComparer.Ordinal).Count() != array.Count ||
            array.Values<string>().Any(id => !Items.Any(i => i.Id == id)))
            throw new AdvisorOptionsException("information_invalid");
        return new AdvisorInformation(array.Values<string>()!);
    }
    internal JArray Excluded(bool battle) => new(Items.Where(i => i.Id.StartsWith(battle ? "battle_" : "campaign_", StringComparison.Ordinal) && !Allows(i.Kind)).Select(i => i.Id));
    internal void AddPolicy(JObject initial, bool battle)
    {
        initial["instructions"]!["information_policy"] =
            "The user selects which automatic data categories may be supplied. Excluded categories are unavailable, not empty or zero. " +
            "Do not request disabled categories, try alternate queries for them, or invent their values. Give conditional advice and explain material limits. " +
            "Published objectives and their public base registry are mandatory. User messages and enabled source documents are verbatim and can mention excluded topics; they are not a complete current status source.";
        initial["situation"]!["coverage"]!["excluded_by_user"] = Excluded(battle);
    }
    internal bool CampaignCategory(string category) => category switch
    {
        "friendly_task_forces" => Allows(InformationKind.CampaignForces),
        "friendly_ships" => Allows(InformationKind.CampaignShips),
        "friendly_air_units" or "friendly_air_flights" => Allows(InformationKind.CampaignAviation),
        "observed_contacts" => Allows(InformationKind.CampaignContacts),
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
        "get_base_observations" => Allows(InformationKind.CampaignBases),
        "list_orders" or "get_orders" => Allows(InformationKind.CampaignOrders),
        "list_reports" or "get_reports" => Allows(InformationKind.CampaignReports),
        _ => throw new AdvisorQueryException("unknown_query", "Use a query from the current catalogue.")
    };
    internal string[] BattleSections => new[] { "fuel", "damage", "weapons" }.Where(s => Allows(s switch
        { "fuel" => InformationKind.BattleFuel, "damage" => InformationKind.BattleDamage, _ => InformationKind.BattleWeapons })).ToArray();
    internal bool BattleCategory(string category) => category switch
    {
        "friendly_units" => Allows(InformationKind.BattleUnits),
        "unit_capabilities" => BattleSections.Length > 0,
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
        "get_unit_capabilities" => BattleSections.Length > 0,
        "list_contacts" => Allows(InformationKind.BattleContacts),
        "get_contact_threats" => Allows(InformationKind.BattleThreats),
        "get_briefing" => Allows(InformationKind.BattleBriefing),
        "list_reports" or "get_reports" => Allows(InformationKind.BattleReports),
        _ => throw new AdvisorQueryException("unknown_query", "Use a query from the current catalogue.")
    };
}
