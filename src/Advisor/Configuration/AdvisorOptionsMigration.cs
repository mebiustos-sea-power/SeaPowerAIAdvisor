using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal static class AdvisorOptionsMigration
{
    internal const int CurrentVersion = 1;
    internal static JObject Upgrade(JObject source) => ConfigMigration.Upgrade(source, CurrentVersion, (value, version) =>
    {
        if (version != 0) throw new AdvisorOptionsException("options_invalid");
        if (value.TryGetValue("disabled_information", out var old))
        {
            if (value.ContainsKey("information_selection") || old is not JArray disabled ||
                disabled.Any(i => i.Type != JTokenType.String) ||
                disabled.Values<string>().Distinct(StringComparer.Ordinal).Count() != disabled.Count ||
                disabled.Values<string>().Any(id => !AdvisorInformation.Items.Any(i => i.Id == id)))
                throw new AdvisorOptionsException("information_invalid");
            var ids = disabled.Values<string>().ToHashSet(StringComparer.Ordinal);
            // The former array made the original categories opt-out; later categories used their defaults.
            value["information_selection"] = new JObject(AdvisorInformation.Items.Select(i => new JProperty(i.Id,
                !ids.Contains(i.Id) && ((int)i.Kind <= (int)InformationKind.CampaignReports || i.DefaultEnabled))));
            value.Remove("disabled_information");
        }
        value.Remove("wip_unit_operations_enabled");
        value["schema_version"] = CurrentVersion;
    });
}
