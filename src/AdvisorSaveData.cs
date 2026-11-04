using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Shared;

namespace SeaPowerAIAdvisor;

// The MOD owns the envelope; each feature owns its entries and any future entry migrations.
internal sealed class AdvisorSaveData
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private readonly SaveDataFormat format;
    internal JObject Data { get; private set; } = new();
    internal SaveDataUpgradeResult? UnsupportedData { get; private set; }
    internal AdvisorSaveData(SaveDataFormat? format = null) => this.format = format ?? new SaveDataFormat(1, 1);
    internal void Reset() { Data = new JObject(); UnsupportedData = null; }
    internal Func<byte[]?>? PrepareCapture(AdvisorOptions options) => options.IncludeModSaveData ? Capture : null;
    internal byte[]? Capture() => Data.HasValues
        ? Utf8.GetBytes(new JObject { ["version"] = format.CurrentVersion, ["data"] = Data.DeepClone() }.ToString(Formatting.None)) : null;
    internal void Restore(byte[]? bytes)
    {
        if (bytes == null) { Reset(); return; }
        using var reader = new JsonTextReader(new StringReader(Utf8.GetString(bytes))) { DateParseHandling = DateParseHandling.None };
        var source = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (reader.Read()) throw new FormatException("Trailing MOD save data.");
        var result = format.Upgrade(source);
        if (result.Document == null)
        {
            Data = new JObject(); UnsupportedData = result;
            return;
        }
        if (result.Document["data"] is not JObject data)
            throw new FormatException("Invalid AI Advisor save data.");
        Data = (JObject)data.DeepClone(); UnsupportedData = null;
    }
}
