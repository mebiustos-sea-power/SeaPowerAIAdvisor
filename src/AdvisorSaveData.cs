using System;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// The MOD owns one document. Features will own their entries and restore their own runtime models.
internal sealed class AdvisorSaveData
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    internal JObject Data { get; private set; } = new();
    internal void Reset() => Data = new JObject();
    internal byte[]? Capture() => Data.HasValues
        ? Utf8.GetBytes(new JObject { ["version"] = 1, ["data"] = Data.DeepClone() }.ToString(Formatting.None)) : null;
    internal void Restore(byte[]? bytes)
    {
        if (bytes == null) { Reset(); return; }
        var document = JObject.Parse(Utf8.GetString(bytes));
        if (document["version"]?.Type != JTokenType.Integer || (int?)document["version"] != 1 || document["data"] is not JObject data)
            throw new FormatException("Unsupported or invalid AI Advisor save data.");
        Data = (JObject)data.DeepClone();
    }
}
