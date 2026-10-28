using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Shared;

namespace SeaPowerAIMissionCreator.Configuration;

// The Creator owns one independent settings document. It never reads Advisor credentials or preferences.
internal sealed class MissionCreatorSettings
{
    internal const int DefaultPort = 18767;
    internal IReadOnlyList<AiConnectionProfile> Connections { get; }
    internal string? SelectedId { get; }
    internal bool DeveloperApiEnabled { get; }
    internal int DeveloperApiPort { get; }
    internal MissionCreatorSettings(IEnumerable<AiConnectionProfile> connections, string? selectedId, bool developerApiEnabled = false, int developerApiPort = DefaultPort)
    {
        Connections = Array.AsReadOnly(connections.ToArray()); AiConnectionProfile.ValidateLibrary(Connections);
        if (selectedId != null && !Connections.Any(c => c.Id == selectedId)) throw new AiConnectionException("api_selection_invalid");
        if (developerApiPort < 1024 || developerApiPort > 65535) throw new AiConnectionException("developer_port_invalid");
        SelectedId = selectedId; DeveloperApiEnabled = developerApiEnabled; DeveloperApiPort = developerApiPort;
    }
    internal AiConnectionProfile Require(string? id = null) => Connections.SingleOrDefault(c => c.Id == (id ?? SelectedId)) ?? throw new AiConnectionException("api_selection_invalid");
    internal JObject Json() => new() { ["schema_version"] = 1, ["connections"] = new JArray(Connections.Select(c => c.Json())),
        ["selected_id"] = SelectedId, ["developer_api_enabled"] = DeveloperApiEnabled, ["developer_api_port"] = DeveloperApiPort };
    internal static MissionCreatorSettings Read(string path)
    {
        if (!File.Exists(path)) return new MissionCreatorSettings(Array.Empty<AiConnectionProfile>(), null);
        if (new FileInfo(path).Length > 262144) throw new AiConnectionException("settings_invalid");
        try
        {
            using var source = new StreamReader(path, new UTF8Encoding(false, true));
            using var reader = new JsonTextReader(source) { DateParseHandling = DateParseHandling.None, MaxDepth = 8 };
            var value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read() || value.Properties().Any(p => p.Name is not ("schema_version" or "connections" or "selected_id" or "developer_api_enabled" or "developer_api_port")) ||
                value["schema_version"]?.Type != JTokenType.Integer || (int)value["schema_version"]! != 1 || value["connections"] is not JArray entries ||
                value["selected_id"]?.Type is not (JTokenType.Null or JTokenType.String) || value["developer_api_enabled"]?.Type != JTokenType.Boolean ||
                value["developer_api_port"]?.Type != JTokenType.Integer) throw new AiConnectionException("settings_invalid");
            var connections = new List<AiConnectionProfile>();
            foreach (var item in entries)
            {
                if (item is not JObject entry || entry.Properties().Any(p => p.Name is not ("id" or "name" or "provider" or "settings" or "request_timeout_seconds" or "max_output_tokens" or "prompt")) ||
                    new[] { "id", "name", "provider", "prompt" }.Any(key => entry[key]?.Type != JTokenType.String) || entry["settings"] is not JObject settings)
                    throw new AiConnectionException("settings_invalid");
                connections.Add(new AiConnectionProfile((string)entry["id"]!, (string)entry["name"]!, (string)entry["provider"]!, settings,
                    AiRequestTimeout.Read(entry), AiOutputTokens.Read(entry), (string)entry["prompt"]!));
            }
            return new MissionCreatorSettings(connections, (string?)value["selected_id"], (bool)value["developer_api_enabled"]!, (int)value["developer_api_port"]!);
        }
        catch (Exception error) when (error is JsonException or OverflowException or DecoderFallbackException)
        { throw new AiConnectionException("settings_invalid"); }
    }
}

internal sealed class MissionCreatorSettingsStore : IAiConnectionStore
{
    internal string FilePath { get; }
    private volatile MissionCreatorSettings current;
    internal MissionCreatorSettings Current => current;
    internal event Action? Changed;
    internal MissionCreatorSettingsStore(string directory)
    { FilePath = Path.Combine(directory, "settings.json"); current = MissionCreatorSettings.Read(FilePath); }
    public IReadOnlyList<AiConnectionProfile> ReadConnections() => AiConnectionProfile.ByName(Current.Connections).ToArray();
    public AiConnectionDraft Create(string pattern)
    {
        var provider = AiProviderDefinition.Require("gemini");
        return new AiConnectionDraft(new AiConnectionProfile(AiConnectionProfile.NewId(), AiConnectionProfile.NewName(Current.Connections.Select(c => c.Name), pattern), provider.Id, provider.Defaults()));
    }
    public AiConnectionDraft Edit(string id) => new(Current.Require(id));
    public void Save(AiConnectionDraft draft, bool adding)
    {
        if (adding == Current.Connections.Any(c => c.Id == draft.Id)) throw new AiConnectionException("api_selection_invalid");
        var entry = draft.Build(); AiProviderDefinition.Require(entry.Provider).Validate(entry.Settings);
        Save(new MissionCreatorSettings(Current.Connections.Where(c => c.Id != entry.Id).Concat(new[] { entry }), Current.SelectedId, Current.DeveloperApiEnabled, Current.DeveloperApiPort));
    }
    public void Duplicate(string id, string suffix)
    { var copy = Current.Require(id).Duplicate(suffix); Save(new MissionCreatorSettings(Current.Connections.Concat(new[] { copy }), Current.SelectedId, Current.DeveloperApiEnabled, Current.DeveloperApiPort)); }
    public void Remove(string id)
    { _ = Current.Require(id); Save(new MissionCreatorSettings(Current.Connections.Where(c => c.Id != id), Current.SelectedId == id ? null : Current.SelectedId, Current.DeveloperApiEnabled, Current.DeveloperApiPort)); }
    internal void Select(string? id) => Save(new MissionCreatorSettings(Current.Connections, id, Current.DeveloperApiEnabled, Current.DeveloperApiPort));
    internal void SaveDeveloper(bool enabled, int port) => Save(new MissionCreatorSettings(Current.Connections, Current.SelectedId, enabled, port));
    private void Save(MissionCreatorSettings value)
    {
        var bytes = Encoding.UTF8.GetBytes(value.Json().ToString(Formatting.Indented) + "\n");
        if (bytes.Length > 262144) throw new AiConnectionException("settings_invalid");
        ConfigFile.Write(FilePath, bytes); current = value; Changed?.Invoke();
    }
}
