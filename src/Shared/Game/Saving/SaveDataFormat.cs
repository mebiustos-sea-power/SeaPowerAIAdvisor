using System;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor.Shared;

// Owners choose the supported range and supply one step per version. Retired steps are removable.
internal sealed class SaveDataFormat
{
    internal int MinimumVersion { get; }
    internal int CurrentVersion { get; }
    private readonly Action<JObject, int>? advance;

    internal SaveDataFormat(int minimumVersion, int currentVersion, Action<JObject, int>? advance = null)
    {
        if (minimumVersion < 1 || currentVersion < minimumVersion)
            throw new ArgumentOutOfRangeException(nameof(minimumVersion));
        MinimumVersion = minimumVersion; CurrentVersion = currentVersion; this.advance = advance;
    }
    internal SaveDataUpgradeResult Upgrade(JObject source)
    {
        var version = Version(source);
        if (version < MinimumVersion) return new SaveDataUpgradeResult(version, null);
        if (version > CurrentVersion)
            throw new FormatException($"Save-data format {version} is newer than supported format {CurrentVersion}.");
        if (version == CurrentVersion) return new SaveDataUpgradeResult(version, source);
        var value = (JObject)source.DeepClone();
        var originalVersion = version;
        while (version < CurrentVersion)
        {
            if (advance == null) throw new InvalidOperationException($"Save-data migration from format {version} is missing.");
            advance(value, version);
            var next = Version(value);
            if (next != version + 1)
                throw new InvalidOperationException("A save-data migration must advance exactly one format version.");
            version = next;
        }
        return new SaveDataUpgradeResult(originalVersion, value);
    }
    private static int Version(JObject value)
    {
        var token = value["version"];
        if (token?.Type != JTokenType.Integer || !int.TryParse(token.ToString(Formatting.None), NumberStyles.None,
            CultureInfo.InvariantCulture, out var version) || version < 1)
            throw new FormatException("Invalid save-data format version.");
        return version;
    }
}

// An unsupported old format is an explicit result; malformed or newer data still raises an error.
internal sealed class SaveDataUpgradeResult
{
    internal int SourceVersion { get; }
    internal JObject? Document { get; }
    internal SaveDataUpgradeResult(int sourceVersion, JObject? document)
    { SourceVersion = sourceVersion; Document = document; }
}
