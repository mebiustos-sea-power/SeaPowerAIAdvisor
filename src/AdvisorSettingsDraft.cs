using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// A local library editor. Neither migration nor editing writes the saved document.
internal sealed class AdvisorSettingsDraft
{
    private readonly JObject source;
    private readonly List<AdvisorApiConfigurationDraft> configurations;
    internal IReadOnlyList<AdvisorApiConfigurationDraft> Configurations => configurations;
    internal string? MainId { get; set; }
    internal string? SubId { get; set; }
    internal bool LogAiJson { get; set; }
    internal AdvisorApiConfigurationDraft Main => Find(MainId) ?? throw new AdvisorConnectionException("api_main_missing");
    internal AdvisorApiConfigurationDraft? Sub => Find(SubId);
    internal AdvisorApiConfigurationDraft? Find(string? id) => id == null ? null : configurations.SingleOrDefault(c => c.Id == id);

    internal AdvisorSettingsDraft(JObject source, string namePattern = "API setting {0}")
    {
        this.source = (JObject)source.DeepClone();
        var library = AdvisorApiLibrary.Read(source, namePattern);
        configurations = library.Configurations.Select(c => new AdvisorApiConfigurationDraft(c)).ToList();
        MainId = library.MainId; SubId = library.SubId;
        LogAiJson = (bool?)source["log_ai_json"] ?? false;
    }

    internal AdvisorApiConfigurationDraft Add(string namePattern = "API setting {0}")
    {
        var number = 1;
        string name;
        do { name = string.Format(System.Globalization.CultureInfo.InvariantCulture, namePattern, number++); }
        while (configurations.Any(c => string.Equals(c.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)));
        var added = new AdvisorApiConfigurationDraft(new AdvisorApiConfiguration("api-" + Guid.NewGuid().ToString("N"), name, "", AdvisorSettings.DefaultModel));
        configurations.Add(added);
        return added;
    }

    internal void Remove(string id)
    {
        var item = Find(id) ?? throw new AdvisorConnectionException("api_selection_invalid");
        configurations.Remove(item);
        // Deleting an assigned entry never silently selects a different credential.
        if (MainId == id) MainId = null;
        if (SubId == id) SubId = null;
    }

    // Tests use the chosen pending entry and saved logging preference, independently of role assignments.
    internal AdvisorSettings TestConfiguration(string id) => AdvisorSettings.Parse(
        AdvisorApiLibrary.Connection(source, (Find(id) ?? throw new AdvisorConnectionException("api_selection_invalid")).Build()).ToString(Formatting.None));
    internal AdvisorSettings TestSettings(bool fallback = false) => TestConfiguration(fallback
        ? SubId ?? throw new AdvisorConnectionException("fallback_key_missing")
        : MainId ?? throw new AdvisorConnectionException("api_main_missing"));

    internal byte[] Data()
    {
        var library = new AdvisorApiLibrary(configurations.Select(c => c.Build()), MainId, SubId);
        var value = library.Document(source, LogAiJson);
        AdvisorSettings.ValidateDraft(value);
        var data = Encoding.UTF8.GetBytes(value.ToString(Formatting.Indented) + Environment.NewLine);
        if (data.Length > AdvisorSettings.MaxDocumentBytes) throw new AdvisorConnectionException("settings_invalid");
        return data;
    }
}
