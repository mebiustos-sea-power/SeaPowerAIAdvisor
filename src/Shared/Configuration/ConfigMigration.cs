using System;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor.Shared;

// Owners supply transformations; the runner neither knows setting fields nor touches files.
internal static class ConfigMigration
{
    internal static int Version(JObject value)
    {
        if (!value.TryGetValue("schema_version", out var token)) return 0;
        if (token.Type != JTokenType.Integer || !int.TryParse(token.ToString(Formatting.None), NumberStyles.None,
            CultureInfo.InvariantCulture, out var version) || version < 1)
            throw new JsonSerializationException("Invalid settings schema version.");
        return version;
    }
    internal static JObject Upgrade(JObject source, int currentVersion, Action<JObject, int> advance)
    {
        if (currentVersion < 1) throw new ArgumentOutOfRangeException(nameof(currentVersion));
        var version = Version(source);
        if (version > currentVersion) throw new JsonSerializationException("Settings were written by a newer version.");
        if (version == currentVersion) return source;
        var value = (JObject)source.DeepClone();
        while (version < currentVersion)
        {
            advance(value, version);
            var next = Version(value);
            if (next <= version || next > currentVersion)
                throw new InvalidOperationException("A settings migration did not advance to a supported schema version.");
            version = next;
        }
        return value;
    }
}
