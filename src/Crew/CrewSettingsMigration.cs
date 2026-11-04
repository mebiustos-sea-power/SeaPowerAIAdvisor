using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Shared;

namespace SeaPowerAIAdvisor.Crew;

internal static class CrewSettingsMigration
{
    internal const int CurrentVersion = 1;
    internal static JObject Upgrade(JObject source) => ConfigMigration.Upgrade(source, CurrentVersion, (value, version) =>
    {
        if (version != 0) throw new CrewException("settings_invalid");
        value["schema_version"] = CurrentVersion;
    });
}
