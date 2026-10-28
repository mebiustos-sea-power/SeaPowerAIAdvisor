using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor.Shared;

// Saved connection values have no feature-owned path, selection or instruction-placement policy.
internal class AiConnectionProfile
{
    internal const int MaxPromptLength = 2000;
    internal string Id { get; }
    internal string Name { get; }
    internal string Provider { get; }
    internal JObject Settings { get; }
    internal int RequestTimeoutSeconds { get; }
    internal int MaxOutputTokens { get; }
    internal string Prompt { get; }
    internal string ApiKey => (string?)Settings["api_key"] ?? "";
    internal string Model => (string?)Settings["model"] ?? "";
    internal AiConnectionProfile(string id, string name, string provider, JObject settings,
        int requestTimeoutSeconds = AiRequestTimeout.DefaultSeconds, int maxOutputTokens = AiOutputTokens.Default, string prompt = "")
    {
        Id = id; Name = name.Trim(); Provider = provider; Settings = (JObject)settings.DeepClone();
        RequestTimeoutSeconds = AiRequestTimeout.Validate(requestTimeoutSeconds); MaxOutputTokens = AiOutputTokens.Validate(maxOutputTokens);
        Prompt = prompt.Trim();
        if (Prompt.Length > MaxPromptLength || Prompt.Any(c => char.IsControl(c) && c != '\r' && c != '\n' && c != '\t'))
            throw new AiConnectionException("prompt_invalid");
        try { _ = new UTF8Encoding(false, true).GetByteCount(Prompt); }
        catch (EncoderFallbackException) { throw new AiConnectionException("prompt_invalid"); }
    }
    internal AiConnectionSettings ToConnectionSettings(bool sub = false) => new(Model, ApiKey, RequestTimeoutSeconds, MaxOutputTokens, Settings, sub);
    internal virtual JObject Json() => new() { ["id"] = Id, ["name"] = Name, ["provider"] = Provider,
        ["settings"] = Settings.DeepClone(), ["request_timeout_seconds"] = RequestTimeoutSeconds,
        ["max_output_tokens"] = MaxOutputTokens, ["prompt"] = Prompt };
    internal AiConnectionProfile Duplicate(string suffix) => new(NewId(), Name + suffix, Provider, Settings, RequestTimeoutSeconds, MaxOutputTokens, Prompt);
    internal static string NewId() => "api-" + Guid.NewGuid().ToString("N");
    internal static string NewName(IEnumerable<string> names, string pattern)
    {
        var existing = new HashSet<string>(names.Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
        var number = 1; string name;
        do { name = string.Format(CultureInfo.InvariantCulture, pattern, number++); } while (existing.Contains(name));
        return name;
    }
    internal static IEnumerable<T> ByName<T>(IEnumerable<T> values) where T : AiConnectionProfile =>
        values.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Id, StringComparer.Ordinal);
    internal static void ValidateLibrary(IReadOnlyList<AiConnectionProfile> values)
    {
        if (values.Any(c => string.IsNullOrWhiteSpace(c.Provider) || c.Provider.Length > 80 || c.Provider.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(c.Id) || c.Id.Length > 80 || c.Id.Any(char.IsControl)) ||
            values.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != values.Count) throw new AiConnectionException("settings_invalid");
        if (values.Any(c => c.Name.Length == 0 || c.Name.Length > 80 || c.Name.Any(char.IsControl)))
            throw new AiConnectionException("api_configuration_name_invalid");
    }
}

internal class AiConnectionDraft
{
    internal string Id { get; }
    internal string Name { get; set; }
    internal string Provider { get; set; }
    internal JObject Settings { get; set; }
    internal int RequestTimeoutSeconds { get; set; }
    internal int MaxOutputTokens { get; set; }
    internal string Prompt { get; set; }
    internal string ApiKey { get => (string?)Settings["api_key"] ?? ""; set => Settings["api_key"] = value.Trim(); }
    internal string Model { get => (string?)Settings["model"] ?? ""; set => Settings["model"] = value.Trim(); }
    internal AiConnectionDraft(AiConnectionProfile value)
    { Id = value.Id; Name = value.Name; Provider = value.Provider; Settings = (JObject)value.Settings.DeepClone();
        RequestTimeoutSeconds = value.RequestTimeoutSeconds; MaxOutputTokens = value.MaxOutputTokens; Prompt = value.Prompt; }
    internal AiConnectionProfile Build() => new(Id, Name, Provider, Settings, RequestTimeoutSeconds, MaxOutputTokens, Prompt);
}

// Feature stores persist their own document and any feature-specific fields.
internal interface IAiConnectionStore
{
    IReadOnlyList<AiConnectionProfile> ReadConnections();
    AiConnectionDraft Create(string namePattern);
    AiConnectionDraft Edit(string id);
    void Save(AiConnectionDraft entry, bool adding);
    void Duplicate(string id, string suffix);
    void Remove(string id);
}
