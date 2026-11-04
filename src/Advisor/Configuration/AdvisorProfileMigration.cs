using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal static class AdvisorProfileMigration
{
    internal const int CurrentVersion = 2;
    internal static JObject Upgrade(JObject source, string initialCustomRole = "") => ConfigMigration.Upgrade(source, CurrentVersion, (value, version) =>
    {
        if (version != 0 || value.Properties().Any(p => p.Name is not ("mode" or "display_name" or "portrait_file" or "personality" or "default_personality" or "custom_personality" or "custom_role")) ||
            value["display_name"]?.Type != JTokenType.String || value["portrait_file"]?.Type != JTokenType.String ||
            (value["mode"] != null && (value["mode"]!.Type != JTokenType.String || (string?)value["mode"] is not ("default" or "custom"))) ||
            new[] { "personality", "default_personality", "custom_personality", "custom_role" }.Any(key => value[key] != null && value[key]!.Type != JTokenType.String))
            throw new AdvisorProfileException("profile_invalid");
        var personality = (string?)value["personality"] ?? "";
        var mode = (string?)value["mode"] ?? "custom";
        var defaultPersonality = (string?)value["default_personality"] ?? personality;
        var adviser = new AdvisorPersona("custom", (string)value["display_name"]!, (string?)value["custom_role"] ?? initialCustomRole,
            (string)value["portrait_file"]!, (string?)value["custom_personality"] ?? personality);
        value.RemoveAll();
        value["schema_version"] = CurrentVersion; value["mode"] = mode;
        value["selected_custom_id"] = adviser.Id; value["default_personality"] = defaultPersonality;
        value["custom_advisors"] = new JArray(adviser.Json());
    });
}
