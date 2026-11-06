using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Shared;

namespace SeaPowerAIAdvisor;

// The MOD owns the envelope and save selection; each feature owns its data format and restoration.
internal sealed class AdvisorSaveData
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private readonly SaveDataFormat format;
    private readonly BattleMissionMessageHistory missionMessages;
    private readonly Crew.CrewSaveData crew;
    internal JObject Data { get; private set; } = new();
    internal AdvisorSaveDataIssue? UnsupportedData { get; private set; }
    internal AdvisorSaveData(SaveDataFormat? format = null, BattleMissionMessageHistory? missionMessages = null, Crew.CrewSaveData? crew = null)
    { this.format = format ?? new SaveDataFormat(1, 1); this.missionMessages = missionMessages ?? new BattleMissionMessageHistory(); this.crew = crew ?? new Crew.CrewSaveData(); }
    internal void Reset() { ResetData(); missionMessages.Clear(); }
    internal void Begin(object mission) { ResetData(); missionMessages.Begin(mission); }
    private void ResetData() { Data = new JObject(); UnsupportedData = null; crew.Reset(); }
    internal Func<byte[]?>? PrepareCapture(AdvisorOptions options) => options.IncludeModSaveData ? Capture : null;
    internal byte[]? Capture()
    {
        var data = (JObject)Data.DeepClone();
        var messages = missionMessages.SaveData();
        if (messages == null) data.Remove("mission_messages");
        else data["mission_messages"] = messages;
        var crewData = crew.Capture();
        if (crewData == null) data.Remove("crew");
        else data["crew"] = crewData;
        return data.HasValues ? Utf8.GetBytes(new JObject { ["version"] = format.CurrentVersion, ["data"] = data }.ToString(Formatting.None)) : null;
    }
    internal void Restore(byte[]? bytes)
    {
        if (bytes == null) { ResetData(); return; }
        using var reader = new JsonTextReader(new StringReader(Utf8.GetString(bytes))) { DateParseHandling = DateParseHandling.None };
        var source = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (reader.Read()) throw new FormatException("Trailing MOD save data.");
        var result = format.Upgrade(source);
        if (result.Document == null)
        {
            crew.Reset(); Data = new JObject(); UnsupportedData = new AdvisorSaveDataIssue("mod", result.SourceVersion);
            return;
        }
        if (result.Document["data"] is not JObject document) throw new FormatException("Invalid AI Advisor save data.");
        var data = (JObject)document.DeepClone();
        var crewIssue = crew.Restore(data["crew"]);
        var messageIssue = missionMessages.Restore(data["mission_messages"]);
        // The feature owns its live history; the envelope does not retain a second, stale copy.
        data.Remove("mission_messages"); data.Remove("crew"); Data = data;
        UnsupportedData = messageIssue != null && crewIssue != null ? new AdvisorSaveDataIssue("mod", result.SourceVersion)
            : messageIssue != null ? new AdvisorSaveDataIssue("mission_messages", messageIssue.SourceVersion)
            : crewIssue != null ? new AdvisorSaveDataIssue("crew", crewIssue.SourceVersion) : null;
    }
}

internal sealed class AdvisorSaveDataIssue
{
    internal string Part { get; }
    internal int SourceVersion { get; }
    internal AdvisorSaveDataIssue(string part, int sourceVersion) { Part = part; SourceVersion = sourceVersion; }
}
